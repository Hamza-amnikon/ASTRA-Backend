"""Generate an explicitly unverified MySQL rehearsal schema from source metadata.

No database connections or data changes are made. Unknown types/expressions abort.
Generated artifacts include a coverage report; they are not cutover approval.
"""
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
TOKEN = re.compile(r"'(?:''|[^'])*'|\b[A-Za-z_][A-Za-z_0-9]*\b")


def ident(value):
    if not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", value):
        raise ValueError("Unsupported identifier")
    return '`' + value + '`'


def name(value):
    return value if len(value) <= 64 else value[:51] + '_' + hashlib.sha256(value.encode()).hexdigest()[:12]


def split_parts(expression):
    parts, start, depth, quoted, i = [], 0, 0, False, 0
    while i < len(expression):
        char = expression[i]
        if char == "'":
            if quoted and i + 1 < len(expression) and expression[i + 1] == "'":
                i += 2
                continue
            quoted = not quoted
        elif not quoted:
            depth += (char == '(') - (char == ')')
            if char == ',' and depth == 0:
                parts.append(expression[start:i].strip())
                start = i + 1
        i += 1
    parts.append(expression[start:].strip())
    return parts


def expression(source, columns, prefix=''):
    value = re.sub(r'::(?:text\[\]|text|uuid|bigint|smallint|integer|regconfig)', '', source)
    # PostgreSQL array predicates become JSON predicates with the same set semantics.
    value = re.sub(r'(\w+)\s*<@\s*ARRAY\[([^\]]*)\]', r'JSON_CONTAINS(JSON_ARRAY(\2), \1)', value)
    value = re.sub(r"('(?:''|[^'])*')\s*=\s*ANY\s*\((\w+)\)", r'JSON_CONTAINS(\2, JSON_QUOTE(\1))', value)
    value = re.sub(r'(\w+)\s*=\s*ANY\s*\(ARRAY\[([^\]]*)\]\)', r'\1 IN (\2)', value)
    value = re.sub(r'(\w+)\s*<>\s*ALL\s*\(ARRAY\[([^\]]*)\]\)', r'\1 NOT IN (\2)', value)
    value = re.sub(r'(\w+)\s*&&\s*(\w+)', r'JSON_OVERLAPS(\1, \2)', value)
    value = re.sub(r"(\w+)\s*~\s*('(?:''|[^'])*')", r"REGEXP_LIKE(\1, \2, 'c')", value)
    value = re.sub(r'\bcardinality\(', 'JSON_LENGTH(', value, flags=re.I)
    value = re.sub(r'\blength\(', 'CHAR_LENGTH(', value, flags=re.I)
    value = re.sub(r'\bTRIM\(BOTH FROM\s+', 'TRIM(', value, flags=re.I)
    if any(fragment in value for fragment in ['::', '<@', '&&', 'ARRAY[', 'ANY (', 'ALL (', ' ~ ']):
        raise ValueError('Unconverted expression: ' + source)
    return TOKEN.sub(lambda m: prefix + ident(m[0]) if m[0] in columns else m[0], value)


def foreign(definition):
    match = re.fullmatch(r'FOREIGN KEY \(([^)]+)\) REFERENCES (\w+)\(([^)]+)\)(.*)', definition)
    if not match:
        raise ValueError('Unsupported foreign key: ' + definition)
    local, table, remote, actions = match.groups()
    local, remote = split_parts(local), split_parts(remote)
    if len(local) != len(remote):
        raise ValueError('Mismatched foreign key columns')
    return local, table, remote, actions


def generate(report):
    if report.get('schemaVersion') != 25 or not report.get('sourceReadOnly'):
        raise ValueError('Expected a read-only schema-25 source inventory')
    tables = report['tables']
    sql = [
        '-- REHEARSAL ONLY: review coverage.json before applying to a new empty database.',
        "SET NAMES utf8mb4 COLLATE utf8mb4_0900_as_cs;",
        "SET SESSION time_zone = '+00:00';",
        "SET SESSION sql_mode = 'STRICT_ALL_TABLES,NO_ZERO_DATE,NO_ZERO_IN_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';",
    ]
    coverage = {'tables': [], 'constraints': [], 'indexes': [], 'textBounds': [], 'foreignKeyAdjustments': [],
                'sequenceDefaults': [], 'blockers': [
                    'Schema must be executed and tested on MySQL before use.',
                    'Every narrowed indexed text field must pass source length preflight before import.',
                    'Full-text, substring and JSON-array search behavior must be verified in the .NET API.',
                    'Audit immutability, triggers/functions, grants, and sequence state require source runtime inspection.',
                    'Data transfer, checksums, attachments, and API parity remain pending.'
                ]}
    checks = {}
    parent_guards = {}
    for table in tables:
        table_name = table['name']
        columns = {c['column_name']: c for c in table['columns']}
        indexed = set()
        for index in table['indexes']:
            if ' USING btree ' in index['definition']:
                indexed |= {m[0] for m in TOKEN.finditer(index['definition'].split(' USING btree ', 1)[1]) if m[0] in columns}
        for constraint in table['constraints']:
            if constraint['type'] == 'f':
                indexed.update(foreign(constraint['definition'])[0])
        definitions = []
        for column in table['columns']:
            col, kind, default = column['column_name'], column['udt_name'], column['column_default']
            serial = bool(default and default.startswith('nextval('))
            if kind == 'text':
                sql_type = 'VARCHAR(255)' if col in indexed else 'LONGTEXT'
                if col in indexed:
                    coverage['textBounds'].append({'table': table_name, 'column': col, 'maximumCharacters': 255})
            else:
                sql_type = {'uuid': 'CHAR(36) CHARACTER SET ascii COLLATE ascii_bin',
                            'bool': 'BOOLEAN', 'int2': 'SMALLINT', 'int4': 'INT', 'int8': 'BIGINT',
                            'jsonb': 'JSON', '_text': 'JSON', '_int2': 'JSON', 'inet': 'VARCHAR(49)',
                            'bytea': 'LONGBLOB', 'timestamptz': 'DATETIME(6)',
                            'time': 'TIME(6)', 'date': 'DATE'}.get(kind)
                if sql_type is None:
                    raise ValueError('Unsupported type: ' + kind)
            value = ident(col) + ' ' + sql_type
            if column['is_nullable'] == 'NO':
                value += ' NOT NULL'
            if serial:
                value += ' AUTO_INCREMENT'
            elif default is not None:
                if default in ('now()', 'CURRENT_TIMESTAMP'):
                    value += ' DEFAULT CURRENT_TIMESTAMP(6)'
                elif default == 'gen_random_uuid()':
                    value += ' DEFAULT (UUID())'
                elif default == "'{}'::text[]":
                    value += ' DEFAULT (JSON_ARRAY())'
                elif default == "'{}'::jsonb":
                    value += ' DEFAULT (JSON_OBJECT())'
                elif 'nextval(' in default:
                    coverage['sequenceDefaults'].append({'table': table_name, 'column': col,
                        'strategy': 'Application must allocate the preserved sequence transactionally.'})
                else:
                    converted = re.sub(r'::(?:text|bigint)', '', default)
                    if not re.fullmatch(r"'(?:''|[^'])*'|true|false|-?\d+", converted):
                        raise ValueError('Unsupported default: ' + default)
                    value += ' DEFAULT ' + ('(' + converted + ')' if sql_type == 'LONGTEXT' else converted)
            definitions.append(value)
            if kind in ('_text', '_int2'):
                checks.setdefault(table_name, []).append((table_name + '_' + col + '_json_array',
                    'JSON_TYPE(' + ident(col) + ") = 'ARRAY'"))
            if kind == 'bool':
                checks.setdefault(table_name, []).append((table_name + '_' + col + '_boolean',
                    ident(col) + ' IN (0, 1)'))
        for constraint in table['constraints']:
            kind, definition = constraint['type'], constraint['definition']
            coverage['constraints'].append({'table': table_name, 'name': constraint['name'], 'type': kind,
                                            'strategy': 'trigger' if kind == 'c' else 'native'})
            if kind in ('p', 'u'):
                match = re.fullmatch(r'(PRIMARY KEY|UNIQUE) \(([^)]+)\)', definition)
                if not match:
                    raise ValueError('Unsupported key: ' + definition)
                key_columns = ', '.join(ident(c) for c in split_parts(match[2]))
                definitions.append(('PRIMARY KEY' if kind == 'p' else 'UNIQUE KEY ' + ident(name(constraint['name']))) + ' (' + key_columns + ')')
            elif kind == 'c':
                if not definition.startswith('CHECK '):
                    raise ValueError('Unsupported constraint: ' + definition)
                checks.setdefault(table_name, []).append((constraint['name'], expression(definition[6:], columns)))
            elif kind != 'f':
                raise ValueError('Unsupported constraint type')
        sql.append('CREATE TABLE ' + ident(table_name) + ' (\n  ' + ',\n  '.join(definitions) +
                   '\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;')
        coverage['tables'].append({'name': table_name, 'columns': len(columns), 'sourceRows': table['rows']})
    # Index expressions preserve partial uniqueness with NULL outside each predicate.
    for table in tables:
        table_name = table['name']
        columns = {c['column_name'] for c in table['columns']}
        constraint_indexes = {c['name'] for c in table['constraints'] if c['type'] in ('p', 'u')}
        for index in table['indexes']:
            record = {'table': table_name, 'name': index['name']}
            coverage['indexes'].append(record)
            if index['name'] in constraint_indexes:
                record['strategy'] = 'primary_or_unique_key'
                continue
            if ' USING gin ' in index['definition']:
                record['strategy'] = 'replacement_search_pending'
                continue
            match = re.fullmatch(r'CREATE (UNIQUE )?INDEX \w+ ON public\.\w+ USING btree \((.*)\)(?: WHERE (.*))?', index['definition'])
            if not match:
                raise ValueError('Unsupported index: ' + index['definition'])
            unique, parts, predicate = match.groups()
            # Greedy capture can absorb the partial predicate. Split it explicitly.
            body = index['definition'].split(' USING btree (', 1)[1]
            if ') WHERE ' in body:
                parts, predicate = body.split(') WHERE ', 1)
            else:
                parts, predicate = body[:-1], None
            converted = []
            for part in split_parts(parts):
                value = expression(part, columns)
                if predicate and unique:
                    value = 'CASE WHEN ' + expression(predicate, columns) + ' THEN ' + value + ' ELSE NULL END'
                converted.append(value if re.fullmatch(r'`\w+`(?: DESC| ASC)?', value) else '(' + value + ')')
            sql.append('CREATE ' + ('UNIQUE ' if unique else '') + 'INDEX ' + ident(name(index['name'])) +
                       ' ON ' + ident(table_name) + ' (' + ', '.join(converted) + ');')
            record['strategy'] = 'conditional_expression' if predicate and unique else 'btree'
    # Foreign keys are added after every referenced table and key exists.
    for table in tables:
        for constraint in table['constraints']:
            if constraint['type'] != 'f':
                continue
            local, parent, remote, actions = foreign(constraint['definition'])
            column_map = {c['column_name']: c for c in table['columns']}
            if 'ON DELETE SET NULL' in actions and any(column_map[c]['is_nullable'] == 'NO' for c in local):
                actions = actions.replace('ON DELETE SET NULL', 'ON DELETE RESTRICT')
                coverage['foreignKeyAdjustments'].append({'table': table['name'], 'name': constraint['name'],
                    'reason': 'PostgreSQL rejects deletion because SET NULL would violate NOT NULL. MySQL requires RESTRICT at definition time.'})
            sql.append('ALTER TABLE ' + ident(table['name']) + ' ADD CONSTRAINT ' + ident(name(constraint['name'])) +
                ' FOREIGN KEY (' + ', '.join(map(ident, local)) + ') REFERENCES ' + ident(parent) +
                ' (' + ', '.join(map(ident, remote)) + ')' + actions + ';')
            if 'ON DELETE SET NULL' in actions:
                # InnoDB cascades bypass child triggers. Guard parent deletes that
                # would turn a valid PostgreSQL row into an invalid child row.
                for check_name, check in checks.get(table['name'], []):
                    referenced = set(re.findall(r'`(\w+)`', check))
                    if not referenced.intersection(local):
                        continue
                    simulated = re.sub(r'`(\w+)`', lambda m: 'NULL' if m[1] in local else 'child.' + m[0], check)
                    where = ' AND '.join('child.' + ident(a) + ' = OLD.' + ident(b) for a, b in zip(local, remote))
                    parent_guards.setdefault(parent, []).append((check_name,
                        'EXISTS (SELECT 1 FROM ' + ident(table['name']) + ' AS child WHERE ' + where + ' AND NOT (' + simulated + '))'))
    search = {'clients': ['name'], 'contacts': ['name'], 'knowledge_articles': ['title', 'body'],
              'ticket_comments': ['body'], 'tickets': ['ticket_number', 'subject', 'description', 'source']}
    for table, columns in search.items():
        sql.append('CREATE FULLTEXT INDEX ' + ident('astra_' + table + '_search') + ' ON ' + ident(table) + ' (' + ', '.join(map(ident, columns)) + ');')
    sql.append('DELIMITER $$')
    for table, rules in checks.items():
        for event in ('INSERT', 'UPDATE'):
            lines = ['CREATE TRIGGER ' + ident(name('astra_' + table + '_' + event.lower() + '_checks')) +
                     ' BEFORE ' + event + ' ON ' + ident(table) + ' FOR EACH ROW BEGIN']
            for rule_name, rule in rules:
                new_rule = re.sub(r'`(\w+)`', r'NEW.`\1`', rule)
                lines.append("  IF NOT (" + new_rule + ") THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: " + rule_name[:105] + "'; END IF;")
            lines.append('END$$')
            sql.append('\n'.join(lines))
    for table, rules in parent_guards.items():
        lines = ['CREATE TRIGGER ' + ident(name('astra_' + table + '_delete_checks')) +
                 ' BEFORE DELETE ON ' + ident(table) + ' FOR EACH ROW BEGIN']
        for rule_name, rule in rules:
            lines.append("  IF " + rule + " THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Referenced constraint: " + rule_name[:95] + "'; END IF;")
        lines.append('END$$')
        sql.append('\n'.join(lines))
    sql.append('DELIMITER ;')
    return '\n\n'.join(sql) + '\n', coverage


if __name__ == '__main__':
    source_path = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'migration/source-schema.json'
    source = json.loads(source_path.read_text())
    schema, coverage = generate(source)
    target = Path(__file__).resolve().parent.parent / 'Migrations'
    target.mkdir(parents=True, exist_ok=True)
    (target / '001_source_schema.sql').write_text(schema)
    (target / 'coverage.json').write_text(json.dumps(coverage, indent=2) + '\n')
    print(f"Generated rehearsal schema for {len(coverage['tables'])} tables, {len(coverage['constraints'])} constraints, {len(coverage['indexes'])} indexes.")
    print('Database execution, data checks and runtime acceptance remain required.')
