[CmdletBinding()]
param(
    [switch]$Upload,
    [switch]$Yes,
    [string]$RepositoryUrl = 'https://github.com/HumulusQ/MDiceV2Public.git',
    [string]$Branch = 'master',
    [string]$Message = 'Update project source files'
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 otherwise encodes piped non-ASCII paths as ASCII.
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputDir = Join-Path $projectRoot 'outputs'
$auditDir = Join-Path $projectRoot ('work\update-audit\' + [guid]::NewGuid().ToString('N'))
$gitDir = Join-Path $auditDir 'repository.git'
$emptyHooks = Join-Path $auditDir 'hooks'
$emptyExcludes = Join-Path $auditDir 'empty-excludes'

# Isolated metadata: never delete, initialize, reset or stage the project's .git.
function Invoke-AuditGit {
    param([string[]]$Arguments)
    $result = @(& git -c core.quotePath=false -c "core.excludesFile=$emptyExcludes" `
        -c core.autocrlf=false -c "core.hooksPath=$emptyHooks" `
        --git-dir="$gitDir" --work-tree="$projectRoot" @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "Git failed (exit $LASTEXITCODE): $($Arguments[0])"
    }
    return $result
}

try {
    if ($Yes -and -not $Upload) { throw '-Yes requires -Upload.' }
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot '.gitignore'))) {
        throw 'Missing .gitignore. Refusing to enumerate/upload unfiltered files.'
    }
    Get-Command git -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path $outputDir, $auditDir, $emptyHooks -Force | Out-Null
    [IO.File]::WriteAllText($emptyExcludes, '')
    & git -c "init.defaultBranch=$Branch" init --bare --quiet "$gitDir"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize isolated audit repository.' }
    Push-Location -LiteralPath $projectRoot
    try {
        $paths = @(Invoke-AuditGit -Arguments @('ls-files', '--others', '--exclude-standard') | Sort-Object)
        $credentialPattern = 'gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
        $textExtensions = @('.cs', '.cpp', '.h', '.ps1', '.bat', '.cmd', '.py', '.sh', '.md',
            '.txt', '.json', '.xml', '.xaml', '.axaml', '.props', '.targets', '.csproj',
            '.vcxproj', '.html', '.js', '.ts', '.lua', '.yaml', '.yml', '.proto', '.sln')
        $rows = @(foreach ($path in $paths) {
            $file = Get-Item -LiteralPath (Join-Path $projectRoot $path)
            # Reject links instead of publishing files outside this project.
            $node = $file
            while ($null -ne $node -and $node.FullName -ne $projectRoot) {
                if (($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Linked path needs manual review: $path"
                }
                if ($node -is [IO.FileInfo]) { $node = $node.Directory } else { $node = $node.Parent }
            }
            if ($file.Length -gt 50MB) { throw "File exceeds upload safety limit (50 MiB): $path" }
            if ($textExtensions -contains $file.Extension.ToLowerInvariant()) {
                if (Select-String -LiteralPath $file.FullName -Pattern $credentialPattern -Quiet) {
                    throw "Possible credential/private key found; review this file: $path"
                }
            }
            [pscustomobject]@{ Path = $path; Bytes = $file.Length; Category = ($path -split '/')[0] }
        })
        if ($rows.Count -eq 0) { throw 'No source files found. Refusing an empty upload.' }
        $rows | Select-Object Path, Bytes | Export-Csv -LiteralPath (Join-Path $outputDir 'update-upload-files.csv') -NoTypeInformation -Encoding UTF8
        $rows.Path | Set-Content -LiteralPath (Join-Path $outputDir 'update-upload-files.txt') -Encoding UTF8
        $totalBytes = ($rows | Measure-Object Bytes -Sum).Sum
        Write-Host ("Source upload preview: {0} files, {1:N2} MiB (uncompressed)" -f $rows.Count, ($totalBytes / 1MB))
        $rows | Group-Object Category | ForEach-Object {
            [pscustomobject]@{ Category = $_.Name; Files = $_.Count; MiB = [math]::Round(($_.Group | Measure-Object Bytes -Sum).Sum / 1MB, 2) }
        } | Format-Table -AutoSize | Out-Host
        Write-Host "Full manifest: $outputDir\update-upload-files.csv"
        Write-Host "Target: $RepositoryUrl ($Branch)"
        Write-Host 'Credential checks are limited; review the manifest and source before public upload.'
        if (-not $Upload) {
            Write-Host 'Preview only. No staging/commit/push or network access was performed.'
            Write-Host 'To upload after reviewing the manifest: update.bat --upload'
        } else {
            # Base the public snapshot on remote history, never local private commits.
            Invoke-AuditGit -Arguments @('remote', 'add', 'origin', $RepositoryUrl) | Out-Null
            $remoteHeads = @(& git --git-dir="$gitDir" ls-remote --exit-code --heads origin "refs/heads/$Branch")
            $remoteExit = $LASTEXITCODE
            if ($remoteExit -eq 0) {
                Invoke-AuditGit -Arguments @('fetch', '--quiet', '--depth=1', 'origin', "refs/heads/$Branch") | Out-Null
                Invoke-AuditGit -Arguments @('update-ref', "refs/heads/$Branch", 'FETCH_HEAD') | Out-Null
            } elseif ($remoteExit -ne 2) {
                throw "Unable to inspect remote branch (exit $remoteExit). No push performed."
            }
            # Rebuild only the isolated index. This also removes old ignored files
            # from the public snapshot without deleting anything in the worktree.
            Invoke-AuditGit -Arguments @('read-tree', '--empty') | Out-Null
            Invoke-AuditGit -Arguments @('add', '--all', '--', '.') | Out-Null
            # Ensure old excluded files did not slip into the staged snapshot.
            $stagedPaths = @(Invoke-AuditGit -Arguments @('ls-files') | Sort-Object)
            if (@(Compare-Object -ReferenceObject $paths -DifferenceObject $stagedPaths).Count -gt 0) {
                throw 'Staged paths differ from the preview. No commit/push performed.'
            }
            # Scan staged bytes too, in case a file changed after the preview.
            & git --git-dir="$gitDir" grep --cached -l -I -E $credentialPattern -- | Out-Host
            if ($LASTEXITCODE -eq 0) { throw 'Possible credentials in staged content. No push performed.' }
            if ($LASTEXITCODE -ne 1) { throw 'Staged credential scan failed. No push performed.' }
            # A public snapshot must not silently erase remote source absent locally.
            # Ignored generated/private files are expected removals; other removals
            # require a separate review, restoration, or a deliberate ignore rule.
            if ($remoteExit -eq 0) {
                $deletions = @(Invoke-AuditGit -Arguments @('diff', '--cached', '--name-only', '--diff-filter=D'))
                $ignoredDeletions = @()
                for ($offset = 0; $offset -lt $deletions.Count; $offset += 50) {
                    $last = [math]::Min($offset + 49, $deletions.Count - 1)
                    $chunk = @($deletions[$offset..$last])
                    $ignoredDeletions += @(& git -c core.quotePath=false -c "core.excludesFile=$emptyExcludes" `
                        --git-dir="$gitDir" --work-tree="$projectRoot" check-ignore --no-index -- @chunk)
                    if ($LASTEXITCODE -notin @(0, 1)) { throw 'Unable to check remote deletion rules. No push performed.' }
                }
                $sourceRemovals = @($deletions | Where-Object { $_ -notin $ignoredDeletions })
                if ($sourceRemovals.Count -gt 0) {
                    $sourceRemovals | Set-Content -LiteralPath (Join-Path $outputDir 'update-remote-source-removal-review.txt') -Encoding UTF8
                    $sourceRemovals | ForEach-Object { Write-Host "Protected remote-only file: $_" }
                    throw 'Remote non-ignored files would be deleted. Review/restore them or deliberately exclude reviewed obsolete files before uploading. No commit/push performed.'
                }
            }
            Invoke-AuditGit -Arguments @('diff', '--cached', '--stat') | Out-Host
            & git --git-dir="$gitDir" diff --cached --quiet
            if ($LASTEXITCODE -eq 0) {
                Write-Host 'Remote source snapshot is already up to date. Nothing to upload.'
            } elseif ($LASTEXITCODE -eq 1) {
                Write-Host 'This synchronizes the public source snapshot, including removals above.'
                if (-not $Yes -and (Read-Host 'Type UPLOAD to commit and push') -cne 'UPLOAD') {
                    Write-Host 'Cancelled. No commit/push performed.'
                } else {
                    Invoke-AuditGit -Arguments @('commit', '-m', $Message) | Out-Host
                    # Fast-forward only; remote races stop rather than overwrite history.
                    Invoke-AuditGit -Arguments @('push', 'origin', "HEAD:refs/heads/$Branch") | Out-Host
                    Write-Host 'Public source upload completed. Project files and local .git were not changed.'
                }
            } else {
                throw 'Unable to inspect staged changes. No push performed.'
            }
        }
    } finally {
        Pop-Location
    }
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
