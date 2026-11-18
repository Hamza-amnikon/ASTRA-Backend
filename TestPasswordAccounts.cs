using System.Text.Json;
using Microsoft.AspNetCore.Identity;

public sealed record TestPasswordLogin(string? Login, string? Password, string? Mode);
public sealed record TestAccountCreate(string Email, string DisplayName, string Password, string Role, string? ClientId = null, string? SupervisorUserId = null);
public sealed record TestPasswordAccount(string Login, string UserId, string DisplayName, string PasswordHash, string Version, string Role);

// Development-only credentials are kept outside web content, with salted password hashes.
public sealed class TestPasswordAccounts
{
    public static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() && configuration.GetValue<bool>("ASTRA_PASSWORD_TEST_LOGIN");
    private readonly string path;
    private readonly object gate = new();
    private readonly PasswordHasher<string> hasher = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> failures = new();

    public TestPasswordAccounts(IConfiguration configuration, IWebHostEnvironment environment)
    {
        path = configuration["ASTRA_TEST_ACCOUNTS_FILE"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "test-accounts.json");
    }

    private List<TestPasswordAccount> Read()
    {
        if (File.Exists(path)) return JsonSerializer.Deserialize<List<TestPasswordAccount>>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Invalid test-account file.");
        return [];
    }

    private TestPasswordAccount Make(string login, string name, string password, string role, string? id = null) =>
        new(login, id ?? Guid.NewGuid().ToString("D"), name, hasher.HashPassword(login, password), Guid.NewGuid().ToString("N"), role);

    private void Write(List<TestPasswordAccount> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) JsonSerializer.Serialize(stream, accounts);
            File.Move(temporary, path, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool IsDemoEmail(string email) =>
        email.Length <= 254 &&
        System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
        string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);

    public static bool IsDemoStaffRole(string role) => role is "TECHNICIAN" or "TEAM_LEAD" or "MANAGER" or "INTERN" or "USER";

    public TestPasswordAccount? Authenticate(TestPasswordLogin input)
    {
        var login = input.Login?.Trim().ToLowerInvariant() ?? "";
        if (login.Length > 254 || input.Mode != "employee" || input.Password is null || input.Password.Length > 200 ||
            !IsDemoEmail(login)) return null;
        lock (gate) {
            var now = DateTimeOffset.UtcNow;
            if (failures.TryGetValue(login, out var failure) && failure.Count >= 5 && failure.Until > now) return null;
            var accounts = Read();
            var account = accounts.SingleOrDefault(a => a.Login == login);
            if (account is null) return null;
            var verified = hasher.VerifyHashedPassword(account.Login, account.PasswordHash, input.Password);
            if (verified == PasswordVerificationResult.Failed) {
                failures[login] = (failure.Until > now ? failure.Count + 1 : 1, now.AddMinutes(5));
                return null;
            }
            failures.Remove(login);
            return account;
        }
    }

    public bool IsCurrent(string? login, string? version)
    {
        lock (gate) return Read().Any(a => a.Login == login && a.Version == version);
    }

    public TestPasswordAccount Prepare(TestAccountCreate account)
    {
        var email = account.Email.Trim().ToLowerInvariant();
        if (!IsDemoEmail(email)) throw new ArgumentException("Enter a valid staff sign-in email address.");
        if (!IsDemoStaffRole(account.Role)) throw new ArgumentException("Choose Technician, Team Lead, Manager or Intern.");
        if (string.IsNullOrWhiteSpace(account.DisplayName) || account.DisplayName.Length > 200 || account.Password.Length is < 12 or > 200)
            throw new ArgumentException("Enter a name and a password of 12–200 characters.");
        lock (gate) {
            if (Read().Any(a => a.Login == email)) throw new InvalidOperationException("An account is already registered for this email. Open People to edit the existing account, or use a different email for a new member.");
            return Make(email, account.DisplayName.Trim(), account.Password, account.Role);
        }
    }

    public void Save(TestPasswordAccount account)
    {
        lock (gate) {
            var accounts = Read();
            if (accounts.Any(a => a.Login == account.Login)) throw new InvalidOperationException("An account is already registered for this email. Open People to edit the existing account, or use a different email for a new member.");
            accounts.Add(account);
            Write(accounts);
        }
    }
}
