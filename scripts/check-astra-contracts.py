"""Check original ASTRA route contracts after moving controllers between API hosts."""
from pathlib import Path
import json,re
root=Path(__file__).resolve().parents[1]/'ASTRA-BACKEND/Astra_backend'
baseline=json.loads((root/'api-contract-baseline.json').read_text())
additions={
    'TicketController.cs': {
        '[HttpGet("views")]',
        '[HttpPost("views")]',
        '[HttpDelete("views/{id:guid}")]',
    }
}
for filename,expected in baseline.items():
    files=list(root.glob('*.API/Controllers/'+filename))
    assert len(files)==1,(filename,'must be owned by one module')
    actual=re.findall(r'\[(?:Route|HttpGet|HttpPost|HttpPatch|HttpPut|HttpDelete)\b[^\]]*\]',files[0].read_text(encoding='utf8'))
    extra=additions.get(filename,set())
    assert all(actual.count(route)==1 for route in extra),(filename,'expected added route missing or duplicated')
    assert [route for route in actual if route not in extra]==expected,(filename,'original route contract changed')
print(f'{len(baseline)} original controller route contracts preserved.')
