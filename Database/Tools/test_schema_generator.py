import copy
import importlib.util
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
spec = importlib.util.spec_from_file_location('generator', ROOT / 'apps/api/Database/Tools/generate-mysql-schema.py')
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class SchemaTranslationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = json.loads((ROOT / 'migration/source-schema.json').read_text())
        cls.sql, cls.coverage = generator.generate(cls.source)

    def test_every_source_table_and_constraint_is_accounted_for(self):
        self.assertEqual(len(self.coverage['tables']), len(self.source['tables']))
        self.assertEqual(len(self.coverage['constraints']), sum(len(t['constraints']) for t in self.source['tables']))
        for table in self.source['tables']:
            self.assertIn('CREATE TABLE `' + table['name'] + '` (', self.sql)

    def test_partial_uniqueness_preserves_predicate_instead_of_broadening_it(self):
        self.assertIn("CASE WHEN (`status` = 'PENDING') THEN `ticket_id` ELSE NULL END", self.sql)
        self.assertIn("CASE WHEN (`parent_id` IS NULL) THEN lower(`name`) ELSE NULL END", self.sql)
        self.assertIn("CASE WHEN (`parent_id` IS NOT NULL) THEN `parent_id` ELSE NULL END", self.sql)

    def test_permissions_use_json_set_containment_and_overlap(self):
        self.assertIn('JSON_CONTAINS(JSON_ARRAY(', self.sql)
        self.assertIn('JSON_OVERLAPS(NEW.`allow_permissions`, NEW.`deny_permissions`)', self.sql)
        self.assertNotIn('<@', self.sql)
        self.assertNotIn('::text', self.sql)

    def test_postgresql_character_length_semantics_are_preserved(self):
        self.assertIn('CHAR_LENGTH(TRIM(NEW.`title`))', self.sql)
        self.assertNotIn('IF NOT ((length(', self.sql)

    def test_cascaded_nulls_have_parent_side_constraint_guards(self):
        self.assertIn('BEFORE DELETE ON `users`', self.sql)
        self.assertIn('BEFORE DELETE ON `departments`', self.sql)
        self.assertIn('EXISTS (SELECT 1 FROM `change_approvals` AS child', self.sql)

    def test_non_nullable_set_null_reference_keeps_deletion_rejected(self):
        self.assertIn('`tickets_category_id_foreign` FOREIGN KEY (`category_id`) REFERENCES `categories` (`id`) ON DELETE RESTRICT', self.sql)
        self.assertEqual(self.coverage['foreignKeyAdjustments'][0]['name'], 'tickets_category_id_foreign')

    def test_binary_data_and_timestamp_precision_are_not_downgraded(self):
        self.assertIn('`content` LONGBLOB', self.sql)
        self.assertIn('`created_at` DATETIME(6)', self.sql)

    def test_unknown_types_stop_conversion(self):
        source = copy.deepcopy(self.source)
        source['tables'][0]['columns'][0]['udt_name'] = 'unsupported_money'
        with self.assertRaises(ValueError):
            generator.generate(source)

    def test_sql_parser_does_not_split_quoted_commas_or_nested_functions(self):
        self.assertEqual(generator.split_parts("id, COALESCE(name, 'a,b'), lower(trim(name))"),
            ['id', "COALESCE(name, 'a,b')", 'lower(trim(name))'])

    def test_untranslated_expressions_are_rejected(self):
        with self.assertRaises(ValueError):
            generator.expression('permissions <@ unsupported::special', {'permissions'})


if __name__ == '__main__':
    unittest.main()
