#!/usr/bin/env sh
# SDK smoke test (§18.9, §29.5): generate C# and TypeScript clients from the committed contract and compile them.
set -eu
cd "$(dirname "$0")"
if [ "${1:-all}" != "ts" ]; then
  (cd cs && dotnet tool restore && dotnet kiota generate -l CSharp -d ../../../openapi/v1.json -c OpenFetchClient -n Sample.Client -o Client --exclude-backward-compatible --clean-output && dotnet build)
fi
if [ "${1:-all}" != "cs" ]; then
  (cd ts && npm install --no-audit --no-fund && npm run smoke)
fi
echo "SDK smoke OK"
