"""Read-only smoke checks for the separate working copy; no database is opened."""
from pathlib import Path
import json, os, subprocess, time, urllib.request, urllib.error

root = Path(__file__).resolve().parents[1] / 'ASTRA-BACKEND' / 'Astra_backend'
modules = json.loads((root / 'moduleManifest.json').read_text())
processes = []
results = []

def get(port, path):
    try:
        with urllib.request.urlopen(f'http://127.0.0.1:{port}{path}', timeout=3) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)

try:
    for index, module in enumerate(modules):
        port = 17401 + index
        name = module['name'] + '.API'
        dll = root / name / 'bin' / 'Debug' / 'net10.0' / (name + '.dll')
        env = dict(os.environ)
        for key in ['SQLSERVER_CONNECTION_STRING','ConnectionStrings__Astra','REDIS_URL','ENTRA_CLIENT_ID','ENTRA_CLIENT_SECRET','ENTRA_TENANT_ID','ASTRA_LOCAL_AUTH_ENABLED']:
            env.pop(key, None)
        env.update(ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=f'http://127.0.0.1:{port}', APP_BASE_URL='http://localhost:5174')
        process = subprocess.Popen(['dotnet', str(dll)], cwd=root/name, env=env,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
        processes.append(process)
        for attempt in range(60):
            try:
                status, data = get(port, '/health')
                break
            except (OSError, TimeoutError):
                if process.poll() is not None: raise RuntimeError(name+' exited during startup')
                time.sleep(.2)
        else: raise RuntimeError(name+' did not start')
        assert status==200 and data['status']=='ok',name
        assert get(port,'/ready')[0]==503,name+' must report missing data dependencies'
        assert get(port,'/tickets')[0]==401,name+' must reject anonymous access'
        status, swagger = get(port,'/swagger/v1/swagger.json')
        assert status==200,name+' API documentation missing'
        paths=set(swagger['paths'])
        assert ('/tickets' in paths)==(module['name']=='TicketService'),name+' leaked ticket endpoints'
        assert ('/auth/status' in paths)==(module['name']=='AuthService'),name+' leaked authentication endpoints'
        if module['name']=='AuthService':
            status, config=get(port,'/auth/status')
            assert status==200 and config['configured'] is False
            assert get(port,'/auth/login')[0]==503
        results.append({'module':name,'health':'passed','anonymous_access':'blocked','route_isolation':'passed','unconfigured_readiness':'503 as expected'})
        process.terminate()
        process.wait(timeout=10)
    print(json.dumps({'passed':len(results),'results':results},indent=2))
finally:
    for process in processes:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill()
