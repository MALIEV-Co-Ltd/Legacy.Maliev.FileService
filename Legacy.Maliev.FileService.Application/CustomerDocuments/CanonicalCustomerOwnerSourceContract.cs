namespace Legacy.Maliev.FileService.Application.CustomerDocuments;

/// <summary>Records the independently read CRM source contract; these pins grant no runtime authority.</summary>
public static class CanonicalCustomerOwnerSourceContract
{
    /// <summary>Gets the source head accepted in the CRM owner readback.</summary>
    public const string AcceptedSourceHead = "10b9a24bc07bf91e2417310bbc9723558583501b";
    /// <summary>Gets the authenticated existence route, without membership or NDA authority.</summary>
    public const string RouteTemplate = "/customers/{id:int}";
    /// <summary>Gets the CRM resource permission required by that source route.</summary>
    public const string Permission = "legacy-customer.customers.read";
    /// <summary>Gets the exact positive integer property used for canonical identity readback.</summary>
    public const string IdentityProperty = "Id";
    /// <summary>Gets the verified CustomersController source SHA256.</summary>
    public const string ControllerSha256 = "8adbda69e3e6055f90e8b2ca1a20b1665702f1cba126318387c56dfa66472496";
    /// <summary>Gets the verified CustomerPermissions source SHA256.</summary>
    public const string PermissionSha256 = "cec08e59670526a20199b81a677fbb86a2970dea139c93ae7e42ccb0a19263a7";
    /// <summary>Gets the verified CustomerModels source SHA256.</summary>
    public const string ModelsSha256 = "c7b3c9a9f793367be4202092210a424e04a4b0bb08b4dee10cd8634d817128d2";
    /// <summary>Gets the verified PascalCase JSON configuration source SHA256.</summary>
    public const string ProgramSha256 = "d161a28da02298348d07b84e0c87e8fa104d89f792b0a952c33408d03054e227";
}
