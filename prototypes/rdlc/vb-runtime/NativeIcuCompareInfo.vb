Imports System
Imports System.Globalization
Imports System.Runtime.CompilerServices
Imports System.Runtime.InteropServices
Imports Microsoft.Win32.SafeHandles

Namespace Microsoft.VisualBasic.CompilerServices
    Friend NotInheritable Class NativeIcuCompareInfo
        Private Const Library As String = "libBCRdlc.IcuBridge.so"
        Private Shared ReadOnly Comparers As New ConditionalWeakTable(Of CultureInfo, NativeIcuCompareInfo)
        Private Shared ReadOnly Initialized As Boolean = InitializeIcu()
        Private ReadOnly Sort As SortHandle

        Private Shared Function InitializeIcu() As Boolean
            If LoadIcu() <> 1 Then
                Throw New PlatformNotSupportedException("RDLC Like requires the pinned .NET native ICU collation shim and a supported ICU installation.")
            End If
            Return True
        End Function

        Private Sub New(ByVal Culture As CultureInfo)
            Dim Pointer As IntPtr
            Dim Status As Integer = GetSortHandle(Culture.CompareInfo.Name, Pointer)
            If Status <> 0 OrElse Pointer = IntPtr.Zero Then
                Throw New PlatformNotSupportedException("ICU could not open RDLC collation for culture '" & Culture.Name & "' (status " & Status.ToString(CultureInfo.InvariantCulture) & ").")
            End If
            Sort = New SortHandle(Pointer)
        End Sub

        Public Shared Function ForCulture(ByVal Culture As CultureInfo) As NativeIcuCompareInfo
            If Culture Is Nothing Then Throw New ArgumentNullException("Culture")
            Return Comparers.GetValue(Culture, Function(Key) New NativeIcuCompareInfo(Key))
        End Function

        Private Shared Sub ValidateOptions(ByVal Options As CompareOptions)
            Const Supported As CompareOptions = CompareOptions.IgnoreCase Or CompareOptions.IgnoreNonSpace Or
                CompareOptions.IgnoreSymbols Or CompareOptions.IgnoreKanaType Or CompareOptions.IgnoreWidth
            If Options <> CompareOptions.Ordinal AndAlso (Options And Not Supported) <> 0 Then
                Throw New ArgumentException("Unsupported RDLC ICU comparison options: " & Options.ToString(), "Options")
            End If
        End Sub

        Public Function Compare(ByVal Left As String, ByVal Right As String,
                                Optional ByVal Options As CompareOptions = CompareOptions.None) As Integer
            ValidateOptions(Options)
            If Options = CompareOptions.Ordinal Then Return String.CompareOrdinal(Left, Right)
            If Left Is Nothing Then Return If(Right Is Nothing, 0, -1)
            If Right Is Nothing Then Return 1
            Return CompareNative(Sort, Left, Left.Length, Right, Right.Length, CInt(Options))
        End Function

        Public Function LastIndexOf(ByVal Source As String, ByVal Value As String,
                                    ByVal Options As CompareOptions) As Integer
            ValidateOptions(Options)
            If Source Is Nothing Then Throw New ArgumentNullException("Source")
            If Value Is Nothing Then Throw New ArgumentNullException("Value")
            If Value.Length = 0 Then Return Source.Length
            If Options = CompareOptions.Ordinal Then Return Source.LastIndexOf(Value, StringComparison.Ordinal)
            Dim MatchedLength As Integer
            Return LastIndexOfNative(Sort, Value, Value.Length, Source, Source.Length, CInt(Options), MatchedLength)
        End Function

        Private NotInheritable Class SortHandle
            Inherits SafeHandleZeroOrMinusOneIsInvalid

            Public Sub New(ByVal Pointer As IntPtr)
                MyBase.New(True)
                SetHandle(Pointer)
            End Sub

            Protected Overrides Function ReleaseHandle() As Boolean
                CloseSortHandle(handle)
                Return True
            End Function
        End Class

        <DllImport(Library, EntryPoint:="BCRdlc_LoadICU", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function LoadIcu() As Integer
        End Function

        <DllImport(Library, EntryPoint:="BCRdlc_GetSortHandle", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi)>
        Private Shared Function GetSortHandle(ByVal LocaleName As String, ByRef Pointer As IntPtr) As Integer
        End Function

        <DllImport(Library, EntryPoint:="BCRdlc_CloseSortHandle", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub CloseSortHandle(ByVal Pointer As IntPtr)
        End Sub

        <DllImport(Library, EntryPoint:="BCRdlc_CompareString", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Unicode)>
        Private Shared Function CompareNative(ByVal Sort As SortHandle, ByVal Left As String, ByVal LeftLength As Integer,
                                              ByVal Right As String, ByVal RightLength As Integer, ByVal Options As Integer) As Integer
        End Function

        <DllImport(Library, EntryPoint:="BCRdlc_LastIndexOf", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Unicode)>
        Private Shared Function LastIndexOfNative(ByVal Sort As SortHandle, ByVal Value As String, ByVal ValueLength As Integer,
                                                  ByVal Source As String, ByVal SourceLength As Integer,
                                                  ByVal Options As Integer, ByRef MatchedLength As Integer) As Integer
        End Function
    End Class
End Namespace
