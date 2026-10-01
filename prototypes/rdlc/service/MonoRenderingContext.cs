using System.Globalization;
using System.Security.Principal;
using System.Threading;

public static class MonoRenderingContext
{
    // Mono loses CultureInfo's nonserialized CultureData across the reporting AppDomain.
    // BC's RPC supplies LCIDs, not custom cultures; re-resolve the corresponding name.
    private static CultureInfo Local(CultureInfo culture)
    {
        return culture == null ? null : CultureInfo.GetCultureInfo(culture.Name);
    }

    public static void SetCulture(CultureInfo culture) { CultureInfo.CurrentCulture = Local(culture); }
    public static void SetUiCulture(CultureInfo culture) { CultureInfo.CurrentUICulture = Local(culture); }
    public static void SetDefaultCulture(CultureInfo culture) { CultureInfo.DefaultThreadCurrentCulture = Local(culture); }

    // Thread.CurrentPrincipal=null makes Mono's cross-domain principal serializer dereference null.
    public static void SetPrincipal(IPrincipal principal)
    {
        Thread.CurrentPrincipal = principal ?? new GenericPrincipal(new GenericIdentity(""), new string[0]);
    }
}
