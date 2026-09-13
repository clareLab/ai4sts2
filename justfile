set shell := ["pwsh", "-NoProfile", "-Command"]
set windows-shell := ["pwsh", "-NoProfile", "-Command"]

sln := "ai4sts2.sln"

export PATH := env("USERPROFILE") + "/.dotnet/tools;" + env("PATH")

default: lint

setup:
    pwsh -NoProfile -File scripts/setup.ps1

restore:
    dotnet restore {{sln}}

build config="Release":
    dotnet build {{sln}} -c {{config}} --nologo

deploy config="Release":
    dotnet build src/Ai4Sts2/Ai4Sts2.csproj -c {{config}} --nologo -p:CopyModOnBuild=true

test:
    dotnet test {{sln}} --nologo

fmt:
    csharpier format .
    dotnet format style {{sln}} --no-restore
    dotnet format analyzers {{sln}} --no-restore
    ruff format scripts
    ruff check --fix scripts

lint:
    csharpier check .
    dotnet format style {{sln}} --verify-no-changes --no-restore
    dotnet build {{sln}} -c Release --nologo -warnaserror
    ruff format --check scripts
    ruff check scripts
    pwsh -NoProfile -File scripts/check-comments.ps1

hook:
    Set-Content -Path .git/hooks/pre-commit -Value "#!/bin/sh`njust lint" -NoNewline

decompile version="0.111.0":
    ilspycmd "$env:STS2_DIR/data_sts2_windows_x86_64/sts2.dll" -p -o ".local/decompiled/sts2-v{{version}}" --nested-directories -r "$env:STS2_DIR/data_sts2_windows_x86_64"

headless action="smoke" instance="dev":
    pwsh -NoProfile -File scripts/headless.ps1 -Action {{action}} -Instance {{instance}}

req op="ping" args="" instance="dev":
    pwsh -NoProfile -File scripts/headless.ps1 -Action request -Op {{op}} -Payload '{{args}}' -Instance {{instance}}

bench rounds="300" encounter="NIBBITS_WEAK" instance="wb":
    python scripts/bench.py bench --instance {{instance}} --encounter {{encounter}} --rounds {{rounds}}

warmup instance="wb" encounter="NIBBITS_WEAK":
    python scripts/bench.py warmup --instance {{instance}} --encounter {{encounter}}

models instance="wb":
    python scripts/bench.py models --instance {{instance}}

migrate:
    python scripts/metrics.py migrate

diff *args:
    python scripts/diff.py {{args}}

monitor port="9418":
    python scripts/monitor.py --port {{port}} --open

report out=".local/report":
    python scripts/monitor.py --export {{out}} --status

archive:
    pwsh -NoProfile -File scripts/archive.ps1
