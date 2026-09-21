#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required for the C# syntax guard." >&2
  exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

sdk_path="$(dotnet --info | awk -F': ' '/Base Path/{gsub(/^[ \t]+|[ \t]+$/, "", $2); print $2; exit}')"
if [[ -z "$sdk_path" ]]; then
  echo "Could not resolve .NET SDK base path." >&2
  exit 1
fi
roslyn_dir="${sdk_path%/}/Roslyn/bincore"
sdk_version="$(basename "${sdk_path%/}")"
sdk_major="${sdk_version%%.*}"
if [[ ! "$sdk_major" =~ ^[0-9]+$ ]]; then
  echo "Could not resolve .NET SDK major version from: $sdk_version" >&2
  exit 1
fi
guard_tfm="net${sdk_major}.0"

if [[ ! -f "$roslyn_dir/Microsoft.CodeAnalysis.dll" || ! -f "$roslyn_dir/Microsoft.CodeAnalysis.CSharp.dll" ]]; then
  echo "Roslyn compiler assemblies not found under $roslyn_dir." >&2
  exit 1
fi

cat >"$tmp/SyntaxGuard.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$guard_tfm</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <LangVersion>latest</LangVersion>
    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.CodeAnalysis" HintPath="$roslyn_dir/Microsoft.CodeAnalysis.dll" />
    <Reference Include="Microsoft.CodeAnalysis.CSharp" HintPath="$roslyn_dir/Microsoft.CodeAnalysis.CSharp.dll" />
  </ItemGroup>
</Project>
EOF

cat >"$tmp/Program.cs" <<'EOF'
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: SyntaxGuard <source-root>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
if (!Directory.Exists(root))
{
    Console.Error.WriteLine("Source root does not exist: " + root);
    return 2;
}

var options = new CSharpParseOptions(
    languageVersion: LanguageVersion.Latest,
    documentationMode: DocumentationMode.Parse,
    kind: SourceCodeKind.Regular);

int files = 0;
int errors = 0;
foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                               .OrderBy(x => x, StringComparer.Ordinal))
{
    files++;
    string text;
    try
    {
        text = File.ReadAllText(file);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{file}: read failed: {ex.Message}");
        errors++;
        continue;
    }

    SyntaxTree tree = CSharpSyntaxTree.ParseText(text, options, path: file);
    foreach (Diagnostic diagnostic in tree.GetDiagnostics())
    {
        if (diagnostic.Severity != DiagnosticSeverity.Error)
            continue;

        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        int line = span.StartLinePosition.Line + 1;
        int column = span.StartLinePosition.Character + 1;
        Console.Error.WriteLine(
            $"{file}({line},{column}): {diagnostic.Id}: {diagnostic.GetMessage()}");
        errors++;
    }
}

if (errors > 0)
{
    Console.Error.WriteLine($"C# syntax guard failed: {errors} error(s) across {files} file(s).");
    return 1;
}

Console.WriteLine($"C# syntax guard passed: {files} source files parsed with Roslyn.");
return 0;
EOF

dotnet run --project "$tmp/SyntaxGuard.csproj" --configuration Release --no-launch-profile -- "$(pwd)/src"
