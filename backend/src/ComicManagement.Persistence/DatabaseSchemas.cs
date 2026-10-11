using System.Collections.ObjectModel;

namespace ComicManagement.Persistence;

public static class DatabaseSchemas
{
    public const string Identity = "identity";
    public const string Catalog = "catalog";
    public const string Reading = "reading";
    public const string Interaction = "interaction";
    public const string Billing = "billing";
    public const string Wallet = "wallet";
    public const string Membership = "membership";
    public const string Missions = "missions";
    public const string Administration = "administration";
    public const string Infrastructure = "infrastructure";

    public static ReadOnlyCollection<string> All { get; } = Array.AsReadOnly(
    [
        Identity,
        Catalog,
        Reading,
        Interaction,
        Billing,
        Wallet,
        Membership,
        Missions,
        Administration,
        Infrastructure,
    ]);
}
