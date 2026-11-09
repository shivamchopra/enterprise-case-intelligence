#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || $1 != /* ]]; then
    echo "Supply an absolute path for a new rehearsal directory." >&2
    exit 2
fi

rehearsal=$1
if ! mkdir "$rehearsal"; then
    echo "The rehearsal directory must be new." >&2
    exit 2
fi

published="$rehearsal/publish"
archive="$rehearsal/review-probe.zip"
extracted="$rehearsal/extracted"
probe=EnterpriseCaseIntelligence.ReviewProbe

dotnet publish "tools/$probe" --configuration Release --no-build --no-restore \
    --no-self-contained -p:UseAppHost=false --output "$published"

shopt -s dotglob nullglob
for member in "$published"/*; do
    if [[ ! -f "$member" || -L "$member" ]]; then
        echo "Unexpected publish entry type. Review the publish output before packaging." >&2
        exit 1
    fi
    case "${member##*/}" in
        "$probe.dll"|"$probe.deps.json"|"$probe.runtimeconfig.json"|"$probe.pdb"|"$probe.staticwebassets.endpoints.json"|web.config) ;;
        *)
            echo "Unexpected publish file. Review the publish output before packaging." >&2
            exit 1
            ;;
    esac
done

for required in "$probe.dll" "$probe.deps.json" "$probe.runtimeconfig.json"; do
    if [[ ! -f "$published/$required" ]]; then
        echo "A required probe file is missing." >&2
        exit 1
    fi
done

(cd "$published" && zip -q "$archive" ./*)
unzip -q "$archive" -d "$extracted"
diff -rq "$published" "$extracted"
echo "Probe ZIP contents match the framework-dependent publish."
