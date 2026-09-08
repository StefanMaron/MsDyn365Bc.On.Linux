using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.CompilerServices;

public sealed class IcuDomainWorker : MarshalByRefObject
{
    public void Run()
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        for (int i = 0; i < 1000; i++)
        {
            if (!StringType.StrLikeText("xIyI", "*\u0131*") ||
                StringType.StrLikeText("I", "i"))
                throw new InvalidOperationException("Turkish Like mismatch across AppDomains");
        }
    }
}

public static class IcuDomainProbe
{
    public static int Main()
    {
        Parallel.For(0, 8, i => {
            var domain = AppDomain.CreateDomain("icu-" + i, null, new AppDomainSetup {
                ApplicationBase = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            });
            try
            {
                var worker = (IcuDomainWorker)domain.CreateInstanceAndUnwrap(
                    typeof(IcuDomainWorker).Assembly.FullName, typeof(IcuDomainWorker).FullName);
                worker.Run();
            }
            finally { AppDomain.Unload(domain); }
        });
        Console.WriteLine("PASS: 8 concurrent AppDomains, 16000 Like assertions, all unloaded");
        return 0;
    }
}
