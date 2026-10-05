# Real isolated Git repositories for the offline File consumer contract only.
function Invoke-FileFixtureGit {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string[]]$Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('-C')
    $start.ArgumentList.Add($Root)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { throw 'Fixture Git timed out.' }
        $text = $output.GetAwaiter().GetResult()
        $null = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Fixture Git failed.' }
        return $text.TrimEnd("`r", "`n")
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function New-FileReleaseSourceFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('legacy-file-source-' + [Guid]::NewGuid().ToString('N'))
    $checkout = Join-Path $root 'checkout'
    $origin = Join-Path $root 'origin.git'
    $null = [IO.Directory]::CreateDirectory($checkout)
    $null = [IO.Directory]::CreateDirectory($origin)
    try {
        $null = Invoke-FileFixtureGit -Root $origin -Arguments @('init', '--bare', '--initial-branch=main')
        $null = Invoke-FileFixtureGit -Root $checkout -Arguments @('init', '--initial-branch=main')
        $null = Invoke-FileFixtureGit -Root $checkout -Arguments @('config', 'user.name', 'Synthetic File Fixture')
        $null = Invoke-FileFixtureGit -Root $checkout -Arguments @('config', 'user.email', 'fixture@example.invalid')
        [IO.File]::WriteAllText((Join-Path $checkout 'source.txt'), 'synthetic committed source')
        $null = Invoke-FileFixtureGit -Root $checkout -Arguments @('add', '--', 'source.txt')
        $null = Invoke-FileFixtureGit -Root $checkout -Arguments @('commit', '-m', 'Synthetic fixture source')
        $originUri = [Uri]::new($origin + [IO.Path]::DirectorySeparatorChar).AbsoluteUri.TrimEnd('/')
        $null = Invoke-FileFixtureGit -Root $checkout -Arguments @('remote', 'add', 'origin', $originUri)
        $null = Invoke-FileFixtureGit -Root $checkout -Arguments @('push', 'origin', 'HEAD:refs/heads/main')
        $commit = Invoke-FileFixtureGit -Root $checkout -Arguments @('rev-parse', 'HEAD')
        return [pscustomobject]@{ Root = $root; Checkout = $checkout; Origin = $origin; OriginUri = $originUri; SourceCommit = $commit }
    } catch {
        Remove-FileReleaseSourceFixture -Root $root
        throw
    }
}

function Remove-FileReleaseSourceFixture {
    param([Parameter(Mandatory)][string]$Root)
    $resolved = [IO.Path]::GetFullPath($Root)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -cne $parent -or
        [IO.Path]::GetFileName($resolved) -cnotmatch '^legacy-file-source-[a-f0-9]{32}$') {
        throw 'Fixture cleanup target is invalid.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

function Move-FileFixtureRemote {
    param([Parameter(Mandatory)]$Fixture)
    $null = Invoke-FileFixtureGit -Root $Fixture.Checkout -Arguments @('commit', '--allow-empty', '-m', 'Synthetic remote advance')
    $null = Invoke-FileFixtureGit -Root $Fixture.Checkout -Arguments @('push', 'origin', 'HEAD:refs/heads/main')
    $null = Invoke-FileFixtureGit -Root $Fixture.Checkout -Arguments @('checkout', '--detach', $Fixture.SourceCommit)
}
