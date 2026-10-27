namespace Astra.Shared.Security;

public static class AccessRules
{
    public static void RequireAdministrator(Actor actor)
    {
        if (!actor.Roles.Contains("ADMIN"))
            throw new ApiFault(403, "Administration access required.");
    }
}
