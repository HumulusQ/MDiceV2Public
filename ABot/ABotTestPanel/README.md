# ABotTestPanel

A test panel project for ABot functionality testing and validation.

## Project Details

- **Type**: .NET 9.0 Class Library
- **Location**: `ABot/ABotTestPanel/`
- **GUID**: `{7D35CA5E-0061-F067-17B7-DFA6F3758F45}`

## Purpose

ABotTestPanel provides utilities and services for testing ABot components and functionality:

- Test service initialization and execution
- ABot test framework integration
- Extensible test panel service via MEF (Managed Extensibility Framework)

## Structure

```
ABotTestPanel/
├── ABotTestPanel.csproj          # Project file (.NET 9.0)
├── TestPanelForm.cs              # Main test panel service with MEF export
├── bin/                           # Output binary files
└── obj/                           # Intermediate build files
```

## Dependencies

- System.ComponentModel.Composition (v6.0.0)

## Building

To build the project:

```powershell
dotnet build ABot/ABotTestPanel/ABotTestPanel.csproj -c Debug
```

To build with the full solution:

```powershell
dotnet build MDiceV2.sln
```

## Integration Notes

- The project is integrated into `MDiceV2.sln`
- It's organized under the "ABot" solution folder
- Build configurations support: Debug|Any CPU, Debug|x64, Debug|x86, Release|Any CPU, Release|x64, Release|x86

## Future Development

This is a foundational test panel project. Future enhancements could include:

- Windows Forms UI for interactive test execution
- Test result reporting and logging
- Integration with MDiceV2 test infrastructure
- ABot.Core interop via P/Invoke or managed wrappers
- Performance testing and benchmarking utilities
