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
    dotnet format style {{sln}} --no-restore
    dotnet format analyzers {{sln}} --no-restore
    csharpier format .
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

solve *args:
    python scripts/solve.py {{args}}

run *args:
    python scripts/run.py {{args}}

pair *args:
    python scripts/pair.py {{args}}

fleet *args:
    python scripts/fleet.py {{args}}

stats *args:
    python scripts/stats.py {{args}}

ladder instance="wb" oracle="dev" tag="ladder":
    python scripts/diff.py --instance {{instance}} --oracle {{oracle}} --encounter NIBBITS_WEAK --encounter CULTISTS_NORMAL --potions FIRE_POTION,BLOCK_POTION --random 3 --steps 30 --restore-every 3 --detour 5
    python scripts/pair.py --instance {{instance}} --encounter CULTISTS_NORMAL --encounter BOWLBUGS_NORMAL --encounter THE_KIN_BOSS --seed AI4STS2,PAIR2,PAIR3
    python scripts/bosslab.py --instance {{instance}} --runs "metrics/runs/2026091[0-3]-1[0-8]*-run-*.json" --config "turns=3,beam=5,maxNodes=2500"
    python scripts/fleet.py --instances {{instance}} --seeds B1-B4 --tag {{tag}}

regress instance="wb" oracle="dev":
    python scripts/diff.py --instance {{instance}} --oracle {{oracle}} --encounter NIBBITS_WEAK --encounter CULTISTS_NORMAL --encounter GREMLIN_MERC_NORMAL --potions FIRE_POTION,BLOCK_POTION --random 5 --steps 40 --restore-every 3 --detour 5
    python scripts/solve.py --instance {{instance}} --encounter NIBBITS_WEAK --encounter CULTISTS_NORMAL --encounter BOWLBUGS_NORMAL --encounter SLIMES_NORMAL --encounter GREMLIN_MERC_NORMAL --encounter AEONGLASS_BOSS --seed AI4STS2,SOLVER2 --potions FIRE_POTION,BLOCK_POTION,STRENGTH_POTION --leaf estimate --beam 4
    python scripts/run.py --instance {{instance}} --seed AI4STS2,RUN3,RUN5 --max-floors 60 --boss --fights 2

monitor port="9418":
    python scripts/monitor.py --port {{port}} --open

report out=".local/report":
    python scripts/monitor.py --export {{out}} --status

archive:
    pwsh -NoProfile -File scripts/archive.ps1
