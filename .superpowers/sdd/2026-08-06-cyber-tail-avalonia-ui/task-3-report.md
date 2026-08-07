# Task 3 Report: Split Workspace View Models

## Scope

- Added the two requested `LogViewViewModel.SyncCollection` characterization tests.
- Split the six existing view-model types from `WorkspaceViewModels.cs` into the four focused files specified by the brief.
- Preserved all type signatures, bindings, command/property names, method bodies, collection properties, and reference-identity lookup behavior.
- Deleted `WorkspaceViewModels.cs` after the split.

## Mechanical verification

The type bodies in the four new files were compared against the corresponding ranges from the original file and are byte-for-byte identical. The six required types are present exactly once, under the requested ownership map.

## Checks

`rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter WorkspaceViewModelTests`

- Blocked during project compilation by the known `CS0433`: `IInteractionContext<TInput, TOutput>` exists in both `ReactiveUI.Core, Version=24.0.0.0` and `ReactiveUI, Version=23.0.0.0`.

`rtk dotnet build src/HexTailSharp.slnx`

- Blocked by the same single `CS0433` conflict in `src/HexTailSharp/MainWindow.axaml.cs`.

`rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj`

- Blocked by the same single `CS0433` conflict in `src/HexTailSharp/MainWindow.axaml.cs`.

The conflict is explicitly deferred to Task 5 by the approved plan; no dependency or UI changes were made here.

## Concerns

No Task 3-specific concerns remain. Test/build pass status is pending Task 5's ReactiveUI conflict resolution.
