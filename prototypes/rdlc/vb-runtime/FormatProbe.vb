Imports System
Imports System.Globalization
Imports System.Threading
Imports Microsoft.VisualBasic

Module FormatProbe
    Function Main() As Integer
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US")
        Dim actual = Format(1234.5D, "#,##0.00")
        Console.WriteLine("Format(1234.5, ""#,##0.00"") = " & actual)
        Return If(actual = "1,234.50", 0, 1)
    End Function
End Module
