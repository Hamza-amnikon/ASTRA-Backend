"""Real HTTP/database checks against the disposable local validation database.

Requires the schema, timezone data and SLA functions in a private MySQL instance.
Never points at the real database machine. Test records live only in the test DB.
"""
import json
from datetime import date,timedelta
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
import uuid
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
BACKEND = ROOT / "ASTRA-BACKEND/Astra_backend"
SOCKET = Path("/tmp/astra-mysql-validation/mysql.sock")
assert SOCKET.exists(), "Start the isolated validation MySQL instance first"

environment = os.environ.copy()
environment.update({
    "ASPNETCORE_ENVIRONMENT": "Development",
    "APP_BASE_URL": "http://localhost:18080",
    "MYSQL_CONNECTION_STRING": f"Server={SOCKET};User ID=root;Database=astra_ticketing;Connection Protocol=UnixSocket;SSL Mode=None",
    "REDIS_URL": "redis://127.0.0.1:6379/15",
    "ASTRA_LOCAL_AUTH_ENABLED": "true",
    "ASTRA_LOCAL_ADMIN_PASSWORD": secrets.token_urlsafe(24),
    "ASTRA_LOCAL_USER_PASSWORD": secrets.token_urlsafe(24),
    "ENTRA_CLIENT_ID": "", "ENTRA_CLIENT_SECRET": "", "ENTRA_TENANT_ID": "",
    "DATA_PROTECTION_KEY_DIRECTORY": "/tmp/astra-mysql-validation/keys",
})
environment.pop("ConnectionStrings__Astra", None)
environment.pop("SQLSERVER_CONNECTION_STRING", None)
modules = json.loads((BACKEND / "moduleManifest.json").read_text())
if "--saved-views-only" in sys.argv:
    modules = [module for module in modules if module["name"] in {"AuthService", "TicketService"}]
ports = {module["name"]: 17401 + i for i, module in enumerate(modules)}
processes = []
cookie = ""
csrf = ""
checks = 0
report_path = f"/service-reports?from={(date.today()-timedelta(days=14)).isoformat()}&to={(date.today()+timedelta(days=1)).isoformat()}"

def request(module, path, method="GET", data=None, workspace="admin", expected=200):
    global checks
    headers = {"Content-Type": "application/json", "Origin": environment["APP_BASE_URL"],
               "x-amnikon-workspace": workspace}
    if cookie: headers["Cookie"] = cookie
    if csrf: headers["x-csrf-token"] = csrf
    req = urllib.request.Request(f"http://127.0.0.1:{ports[module]}{path}",
        data=None if data is None else json.dumps(data).encode(), headers=headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    body = response.read()
    if response.status != expected:
        raise AssertionError(f"{method} {module}{path}: {response.status}; {body.decode()}")
    checks += 1
    print(f"PASS {method} {module}{path}", flush=True)
    result = json.loads(body) if body and 'json' in response.headers.get('Content-Type', '') else body
    return result, response.headers

try:
    for module in modules:
        name = module["name"]
        log = open(f"/tmp/astra-mysql-validation/{name}.log", "w")
        env = environment | {"ASPNETCORE_URLS": f"http://127.0.0.1:{ports[name]}"}
        process = subprocess.Popen(["dotnet", str(BACKEND / f"{name}.API/bin/Debug/net10.0/{name}.API.dll")],
            cwd=BACKEND / f"{name}.API", env=env, stdout=log, stderr=subprocess.STDOUT)
        processes.append((process, log))
    for name in ports:
        for attempt in range(80):
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{ports[name]}/health", timeout=1): break
            except OSError: time.sleep(0.25)
        else: raise AssertionError(f"{name} did not start")
        request(name, "/ready")

    _, headers = request("AuthService", "/auth/local-login", "POST",
        {"username": "admin", "password": environment["ASTRA_LOCAL_ADMIN_PASSWORD"]})
    cookie = headers["Set-Cookie"].split(";", 1)[0]
    view, _ = request("AuthService", "/auth/me")
    csrf = view["csrfToken"]

    saved_name = "Integration filters " + secrets.token_hex(4)
    saved, _ = request("TicketService", "/tickets/views", "POST",
        {"name": saved_name, "query": {"q": "system", "status": "NEW", "priority": "P3_MEDIUM"}}, workspace="operations")
    saved_id = saved["id"]
    assert saved["query"]["q"] == "system", "Saved filters must be JSON objects"
    changed, _ = request("TicketService", "/tickets/views", "POST",
        {"name": saved_name, "query": {"q": "updated system", "status": "OPEN"}}, workspace="operations")
    assert changed["id"] == saved_id, "Saving the same name must update the existing view"
    views, _ = request("TicketService", "/tickets/views", workspace="operations")
    assert len([item for item in views if item["id"] == saved_id]) == 1
    assert next(item for item in views if item["id"] == saved_id)["query"]["status"] == "OPEN"
    for payload in [
        {"name": "x", "query": {}},
        {"name": saved_name, "query": {"status": "INVALID"}},
        {"name": saved_name, "query": {"userId": "someone-else"}},
        {"name": saved_name, "query": {"clientId": "not-a-guid"}},
        {"name": saved_name, "query": {"q": {"nested": "invalid"}}},
    ]:
        request("TicketService", "/tickets/views", "POST", payload, workspace="operations", expected=400)
    other_view = str(uuid.uuid4())
    client = ["mysql", f"--socket={SOCKET}", "-u", "root", "astra_ticketing"]
    subprocess.run(client + ["-e", f"INSERT INTO saved_ticket_views(id,user_id,name) VALUES('{other_view}','10000000-0000-4000-8000-000000000002','Other account test');"], check=True)
    try:
        views, _ = request("TicketService", "/tickets/views", workspace="operations")
        assert all(item["id"] != other_view for item in views), "Another account's views must not be visible"
        request("TicketService", "/tickets/views/" + other_view, "DELETE", workspace="operations", expected=404)
    finally:
        subprocess.run(client + ["-e", f"DELETE FROM saved_ticket_views WHERE id='{other_view}';"], check=True)
    request("TicketService", "/tickets/views/" + saved_id, "DELETE", workspace="operations")
    request("TicketService", "/tickets/views/" + saved_id, "DELETE", workspace="operations", expected=404)
    if "--saved-views-only" in sys.argv:
        print(f"Completed {checks} saved-view API and ownership checks", flush=True)
        raise SystemExit(0)

    for resource in ["clients", "contacts", "sites", "assets", "categories", "tags", "users", "roles", "permissions", "ticket-sources", "audit-logs"]:
        request("CoreDataService", "/" + resource)
    staff, _ = request("CoreDataService", "/internal/directory", workspace="operations")
    assert isinstance(staff, list)
    teams, _ = request("CoreDataService", "/internal/departments", workspace="operations")
    assert isinstance(teams, list) and teams
    request("AdministrationService", "/admin/overview")
    for resource in ["clients", "contacts", "sites", "assets", "categories", "tags", "sources", "departments", "users", "audit-logs"]:
        request("AdministrationService", "/admin/" + resource + "?q=test&page=1&pageSize=5")
    request("GroupService", "/admin/groups/options")
    request("GroupService", "/admin/groups")
    request("ClientDomainService", "/admin/client-domains")
    policies, _ = request("SlaService", "/sla/policies")
    calendars, _ = request("SlaService", "/sla/calendars")
    request("NotificationService", "/notifications")
    prefs = dict(ticketAssigned=True, ticketTransferred=True, publicReply=True, slaAtRisk=True, slaBreached=True, ticketUnattended=True)
    request("NotificationService", "/notifications/preferences", "PATCH", prefs)
    request("NotificationService", "/notifications/preferences")
    request("NotificationService", "/notifications/read-all", "PATCH", {})
    request("ReportService", report_path)
    request("TicketService", "/tickets/options")
    request("TicketService", "/tickets?scope=all")
    request("TicketService", "/tickets/dashboard?all=true")
    request("ProblemService", "/problems/options")
    request("ProblemService", "/problems")

    client, _ = request("CoreDataService", "/clients", "POST", {"name": "MySQL integration customer " + secrets.token_hex(4), "ticketPrefix": "MT" + secrets.token_hex(3).upper()}, expected=201)
    client_id = client["id"]
    contact, _ = request("CoreDataService", "/contacts", "POST", {"clientId": client_id, "name": "Integration requester", "email": "requester@example.test"}, expected=201)
    site, _ = request("CoreDataService", "/sites", "POST", {"clientId": client_id, "name": "Integration site"}, expected=201)
    group_input = dict(name="Integration staff " + secrets.token_hex(4), description="Validation only", kind="STAFF",
        permissions=["REPORTS_VIEW"], memberIds=["10000000-0000-4000-8000-000000000001"], active=True)
    group, _ = request("GroupService", "/admin/groups", "POST", group_input, expected=201)
    request("GroupService", "/admin/groups/" + group["id"])
    request("GroupService", "/admin/groups?q=Integration")
    group_input["revision"] = group["revision"]
    group_input["description"] = "Updated validation group"
    request("GroupService", "/admin/groups/" + group["id"], "PATCH", group_input)
    domain_input = dict(clientId=client_id,siteId=site["id"],domain=secrets.token_hex(4)+".example.test",active=True)
    request("ClientDomainService", "/admin/client-domains", "POST", domain_input, expected=201)
    request("ClientDomainService", "/admin/client-domains")
    calendar_input = dict(name="Integration calendar " + secrets.token_hex(4),timezone="Asia/Kolkata",workingDays=[1,2,3,4,5],workStart="09:00",workEnd="18:00",active=True,holidays=[])
    calendar, _ = request("SlaService", "/sla/calendars", "POST", calendar_input, expected=201)
    request("SlaService", "/sla/calendars/"+calendar["id"], "PATCH", calendar_input)
    options, _ = request("TicketService", "/tickets/options", workspace="operations")
    assert any(item["id"] == client_id for item in options["clients"]), "Local admin operational client scopes missing"
    category = options["categories"][0]["id"]
    ticket, _ = request("TicketService", "/tickets", "POST", {
        "clientId": client_id, "requesterId": contact["id"], "siteId": site["id"], "categoryId": category,
        "subject": "MySQL integration ticket", "description": "Created only in the isolated validation database",
        "source": options["sources"][0]["code"], "priority": "P3_MEDIUM",
        "assignedUserId": "10000000-0000-4000-8000-000000000001",
    }, workspace="operations", expected=201)
    ticket_id = ticket["id"]
    request("TicketService", "/tickets/" + ticket_id)
    mail, _ = request("TicketService", "/mail/tickets/" + ticket_id, workspace="operations")
    assert mail == [], "A new ticket must have no fabricated email messages"
    request("TicketService", "/mail/tickets/" + str(uuid.uuid4()), workspace="operations", expected=404)
    request("TicketService", "/mail/attachments/" + str(uuid.uuid4()), workspace="operations", expected=404)
    attachment, _ = request("TicketService", "/tickets/" + ticket_id + "/attachments", "POST", dict(fileName="validation.txt",contentType="text/plain",contentBase64="aGVsbG8=",visibility="INTERNAL"),workspace="operations")
    content, _ = request("TicketService", "/tickets/" + ticket_id + "/attachments/" + attachment["id"])
    assert content == b"hello", "Attachment data was not preserved"
    problem, _ = request("ProblemService", "/problems", "POST", dict(clientId=client_id,siteId=site["id"],title="MySQL integration problem",description="Isolated problem workflow validation",priority="P3_MEDIUM"), workspace="operations")
    request("ProblemService", "/problems/"+problem["id"])
    request("ProblemService", "/problems/"+problem["id"]+"/tickets", "POST",dict(ticketId=ticket_id),workspace="operations")
    request("ProblemService", "/problems/"+problem["id"]+"/notes", "POST",dict(body="Integration problem note"),workspace="operations")
    request("TicketService", "/tickets/" + ticket_id, "PATCH", {"status": "IN_PROGRESS"}, workspace="operations")
    request("TicketService", "/tickets/" + ticket_id + "/comments", "POST", {"type": "PUBLIC", "body": "Integration reply"}, workspace="operations")
    request("TicketService", "/tickets/" + ticket_id, "PATCH", {"status": "ON_HOLD"}, workspace="operations")
    request("TicketService", "/tickets/" + ticket_id, "PATCH", {"status": "IN_PROGRESS", "priority": "P2_HIGH"}, workspace="operations")
    request("TicketService", "/tickets/" + ticket_id, "PATCH", {"status": "RESOLVED"}, workspace="operations")
    request("TicketService", "/tickets/" + ticket_id)
    request("TicketService", "/tickets/dashboard?all=true")
    request("ReportService", report_path)
    request("AdministrationService", "/admin/audit-logs?q=ticket")
    print(f"Completed {checks} live MySQL/HTTP checks", flush=True)
finally:
    if cookie and csrf:
        try: request("AuthService", "/auth/logout", "POST", {})
        except Exception: pass
    for process, _ in processes:
        process.terminate()
    for process, log in processes:
        try: process.wait(timeout=10)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
        log.close()
