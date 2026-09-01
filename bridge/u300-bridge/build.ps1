# Builds the U300 bridge into build/u300-bridge.jar
# Requires a JDK 11 or later on PATH (or JAVA_HOME set). No build tool needed.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$javac = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\javac.exe' } else { 'javac' }
$jar   = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\jar.exe' }   else { 'jar' }

$classes = Join-Path $root 'build\classes'
$out     = Join-Path $root 'build'

Remove-Item -Recurse -Force $classes -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $classes | Out-Null

$libs = (Get-ChildItem (Join-Path $root 'libs') -Filter *.jar | ForEach-Object { $_.FullName }) -join ';'
$sources = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.java | ForEach-Object { $_.FullName }

Write-Host "Compiling $($sources.Count) source file(s)..."
& $javac --release 11 -Xlint:-options -cp $libs -d $classes @sources
if ($LASTEXITCODE -ne 0) { throw "javac failed with exit code $LASTEXITCODE" }

# Class-Path entries resolve relative to the jar, not the working directory,
# so the dependencies are copied in beside it. build\ is then a self-contained
# deployable: copy the folder, run the jar, done.
New-Item -ItemType Directory -Force -Path (Join-Path $out 'libs') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $out 'native') | Out-Null
Copy-Item (Join-Path $root 'libs\*.jar') (Join-Path $out 'libs') -Force
Copy-Item (Join-Path $root 'native\*') (Join-Path $out 'native') -Force -ErrorAction SilentlyContinue

$cp = (Get-ChildItem (Join-Path $out 'libs') -Filter *.jar | ForEach-Object { "libs/$($_.Name)" }) -join ' '

$manifest = Join-Path $out 'MANIFEST.MF'
@(
  "Manifest-Version: 1.0"
  "Main-Class: com.warehouse.u300bridge.Main"
  "Class-Path: $cp"
  ""
) | Set-Content -Path $manifest -Encoding ASCII

& $jar --create --file (Join-Path $out 'u300-bridge.jar') --manifest $manifest -C $classes .
if ($LASTEXITCODE -ne 0) { throw "jar failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Built: $(Join-Path $out 'u300-bridge.jar')"
Write-Host "Run:   java -jar build/u300-bridge.jar bridge.properties"
