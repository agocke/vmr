Imports System
Imports System.Reflection
Imports System.Resources
Imports System.Runtime.CompilerServices

Public Module Program
#If BROKEN Then
    Private Const BrokenValue As Integer = "intentional VB compiler failure"
#End If
    Private Function SourcePath(<CallerFilePath> Optional path As String = "") As String
        Return path
    End Function

    Public Sub Main()
#If EXPECTED <> 42 Then
#Error Declared compiler constants were lost.
#End If
        Dim resources As New ResourceManager("Fixture.Root.Strings", Assembly.GetExecutingAssembly())
        Console.WriteLine(SourcePath() & ";" & Producer.Text & ";" & resources.GetString("Greeting"))
    End Sub
End Module
