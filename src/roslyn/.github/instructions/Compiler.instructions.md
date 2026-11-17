---
applyTo: "src/{Compilers,Dependencies,ExpressionEvaluator,Tools}/**/*.{cs,vb}"
---

# Roslyn Compiler Instructions for AI Coding Agents

## Architecture Overview

Roslyn follows a **layered compiler architecture**:
- **Lexer → Parser → Syntax Trees → Semantic Analysis → Lowering/Rewriting → Symbol Tables → Emit**
- Core abstraction: `Compilation` is immutable and reusable. Create new compilations via `AddSyntaxTrees()`, `RemoveSyntaxTrees()`, `ReplaceSyntaxTree()` for incremental changes
- **Internal vs Public APIs**: Use `InternalSyntax` namespace for performance-critical parsing; `Microsoft.CodeAnalysis` for public consumption

### Key Directories
- `src/Compilers/Core/Portable/` - Language-agnostic compiler infrastructure
- `src/Compilers/CSharp/Portable/` - C# compiler implementation  
- `src/Compilers/VisualBasic/Portable/` - VB compiler implementation
- `src/Compilers/Server/` - `VBCSCompiler` build server
- `src/Dependencies/` - High-performance collections (`PooledObjects`, `Threading`)
- `src/ExpressionEvaluator/` - Debugger expression evaluation (uses special `LexerMode.DebuggerSyntax`)
- `src/Tools/` - Compiler and infrastructure tooling (BuildBoss, `dotnet-roslyn-tools`, format tools, analyzers) and benchmark harnesses; `IdeCoreBenchmarks/BenchmarkDotNet.Directory.Packages.props` supplies the package policy copied beside generated BDN runners

### Essential Files for Context
- `src/Compilers/CSharp/Portable/Errors/ErrorCode.cs` - All C# compiler error codes
- `src/Compilers/CSharp/Portable/Errors/MessageID.cs` - Language feature version gating
- `src/Compilers/CSharp/Portable/LanguageVersion.cs` - Public language-version enum and parsing/mapping logic
- `src/Compilers/CSharp/Portable/Syntax/Syntax.xml` - Syntax tree node definitions (generated code source)
- `src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml` - Bound tree node definitions (generated code source)
- `docs/wiki/Roslyn-Overview.md` - Architecture deep-dive

## Declared compiler dependency inputs

The opt-in declared-I/O compiler targets import `CompilerInputs.props`
(`CompilerInputsPath` can override its location). genbuild publishes this
relocatable catalog from its compiler and SDK-analyzer copy items.
`ResolveCompilerDependencies` is a filesystem-free selector over candidate files
and the final `Analyzer` items; run it inside `CoreCompile` after `BeforeTargets`
hooks, not as an earlier dependency target. Loose/generated analyzer companions
require explicit `AnalyzerDependency` items. Acquired package and framework-pack
inventories supply the remaining candidates; compiler targets need no per-project
Restore manifest.

## Code Generation

Several core data structures are generated from XML definitions — **never edit the generated `.cs` or `.vb` files directly**:
- **Syntax trees**: `src/Compilers/CSharp/Portable/Syntax/Syntax.xml`
- **Bound trees**: `src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml`
- After modifying these XML files, regenerate and build:
  ```bash
  dotnet run --file eng/generate-compiler-code.cs
  dotnet build src/Compilers/{CSharp,VisualBasic}/Portable # choose the project matching the C# or VB syntax you changed
  ```

## Conventions

- **MEF is not used in the compiler layer.** `ExportLanguageService` / `ImportingConstructor` and the IDE service model are IDE-layer concepts — ignore them here.
- **Null checks**: validate internal-API preconditions with `Debug.Assert(...)` (a violated internal precondition may NRE in release); validate public APIs with explicit null checking when appropriate, throwing a dedicated exception with a localized string.
- **Immutability** is via `Compilation` (`AddSyntaxTrees`/`RemoveSyntaxTrees`/`ReplaceSyntaxTree`), not the workspace `Document`/`Solution` model.

## Essential Patterns

### Memory Management
- **Avoid LINQ in hot paths** - use manual enumeration or `struct` enumerators
- **Avoid `foreach` over collections without struct enumerators** 
- **Use object pools extensively** - see patterns in `src/Dependencies/PooledObjects/`
- **Prefer `Debug.Assert()` over exceptions** for internal validation

## Build & Test Workflows

### Essential Build Commands

```powershell
# Full build (use VS Code tasks when available)
./build.sh

# Build specific components  
dotnet build Compilers.slnf                    # Compiler-only build
dotnet build src/Compilers/CSharp/csc/AnyCpu/  # C# compiler

# Generate compiler code after changes
dotnet run --file eng/generate-compiler-code.cs
```

## Debugger Integration

**Expression Evaluator** uses special parsing modes:
- `LexerMode.DebuggerSyntax` for expression evaluation
- `IsInFieldKeywordContext` flag for context-aware parsing
- `ConsumeFullText` parameter for complete expression parsing

## MSBuild Integration

Compiler tasks are in `src/Compilers/Core/MSBuildTask/`:
- `Csc.cs` - C# compiler task
- `Vbc.cs` - VB compiler task  
- `ManagedCompiler.cs` - Base compiler task functionality
- Hardened C# and VB compilation use zero-I/O `FormatCsc`/`FormatVbc` and the
  independent `CompileManaged` action, not server/IDE/tool-discovery execution. Both
  deterministic compilation and deterministic source paths are mandatory.
  Automatic mappings preserve SourceLink root aliases and cover project,
  generated-source and output paths, including CodeView. Both compilers consume
  the packaged `CompilerInputs.props` catalog directly. Final analyzer dependency
  selection uses acquired package/framework inventories and explicit
  `AnalyzerDependency` items, without per-project compiler snapshot imports or
  directory discovery.
  Declare analyzer/generator dependencies, configs and additional files and
  trust their nominal compiler-input-only contract; enforcement is deferred
  without per-plugin gates. `CompileManaged` clears the child environment, then
  forwards only `SystemRoot`, `TMPDIR`, `TMP`, `TEMP` and
  `DOTNET_EnableDiagnostics` from `TaskEnvironment`; action execution receives
  only explicitly selected `MSBuildActionEnvironment` values, not ambient
  startup hooks or compiler/runtime-injection settings. It uses ToolTask
  cancellation/private response-file scratch, never server state
  or a task-local cache. Shared `CompilerInvocation` composes footprint validation
  and path mappings without another compiler inheritance layer. VB requires an
  explicit runtime path, `None`/`Embed`, or `NoVBRuntimeReference`; implicit runtime
  and SDK-directory searches are rejected. Keep ordinary Csc/Vbc behavior unchanged.
- On modern .NET/Linux, the action host stages every admitted `CompileManaged`
  action, independently of `MSBuildSandboxToolTasks`. Source Utilities'
  `SandboxedToolTask` reuses that context without nesting staging; its standalone
  opt-in mode remains available. Formatters always regenerate response
  templates on the supported execution platform from typed properties with
  explicit input/output bindings; never replace original path strings in opaque
  switches, resource names or file contents. Map executable/compiler paths, cwd
  and stable path maps, with private
  response/temp scratch and validated file publication. Analyzer configs are
  ordinary opaque input files: map their command-line paths without inspecting
  or rewriting contents. Config producers must adapt raw-path dependencies.
  Staged C# generated trees reuse archive/materializer publication: map only the
  physical archive output, retain `/generatedfilesout` as a logical namespace,
  and map generated logical paths as well as staged source paths to stable
  aliases. The host writes dyndep after task success, before publishing any
  declared output; exclude that sidecar from
  `GetToolOutputs`, not from the declared graph footprint. VB archive mode is
  unsupported. Symlink staging is not filesystem/network confinement or
  read-only input enforcement. Compiler-task builds require source Utilities'
  `netstandard2.0` assembly and source Framework contract first
  (`GenBuildMSBuildUtilitiesAssembly` and `GenBuildMSBuildFrameworkAssembly`
  override their paths); genbuild's component pipeline supplies them. The
  standalone app ships the modern implementation rather than the compile-time
  compatibility binary.
- Hardened `EmitCompilerGeneratedFiles` declares one compiler-owned output tree.
  The source-built C# compiler's archive mode redirects physical writes only;
  preserve `/generatedfilesout` logical paths, analyzer-config matching and PDB
  embedding. ZIP entries retain ordinary source encodings and use ordinal order
  and fixed timestamps. The standalone action host emits dyndep after compiler
  success and independently materializes receipt-owned members. Do not run
  generators twice, redirect logical paths to scratch, or enumerate future
  generated files during construction. Ordinary compiler persistence is unchanged.
- `MapSourceRoots` is an audited zero-I/O construction task with explicit empty
  `DeclaredInputs` and `DeclaredOutputs` at its shipped call site. It computes
  mapped paths from supplied root metadata and `TaskEnvironment` project context;
  source-control discovery must already have prepared those inputs.
- `Microsoft.Managed.Core.targets` prepares the analyzer-skip marker during
  ordinary Restore when `MSBuildRestoreGraphInputs=true`. Hardened graph
  construction consumes that marker without initializing it. Skipped-analyzer
  builds publish an empty marker through `WriteLinesToFile`, with the intermediate
  assembly as its declared input so publication follows successful compilation.
  The marker is not also a compiler input in that configuration. Ordinary builds
  retain `Touch`; its clock access and preservation of prior contents are not
  asserted as a deterministic declared-I/O contract.
- Hardened editorconfig generation composes the zero-I/O `FormatAnalyzerConfig`
  task with the overwrite-only declared-output `WriteAnalyzerConfig` action.
  Ordinary builds retain `GenerateMSBuildEditorConfig`; both paths share the
  serializer. The formatter resolves paths from project context and rejects
  filesystem timestamp metadata. Keep caching and write-if-different
  publication policies out of the hardened tasks.

## Performance Considerations

1. **Lexer/Parser optimizations**: Use `InternalSyntax` types for performance-critical code
2. **Immutable data structures**: Roslyn heavily uses immutable collections and copy-on-write semantics
3. **Caching**: `Compilation` objects cache semantic information - reuse when possible
4. **Threading**: Most compiler operations are thread-safe through immutability

## Symbol Resolution

Navigate the symbol hierarchy:
```cs
var compilation = CreateCompilation(source);
var globalNamespace = compilation.GlobalNamespace;
var typeSymbol = globalNamespace.GetTypeMembers("MyClass").Single();
var methodSymbol = typeSymbol.GetMembers("MyMethod").Single();
```

Symbol equality is complex due to generics and substitution - always test with multiple generic scenarios.
