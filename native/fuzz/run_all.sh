#!/usr/bin/env bash
# Runs every fuzz target in parallel for SECONDS (default 300), each with its seeds and the JSON
# dictionary, continuing past crashes (-fork). Inputs that crash land in artifacts/<target>/;
# logs in LOGDIR (default target/fuzz-logs). Build first: cargo +nightly fuzz build -O
#
#   ./run_all.sh 600
set -euo pipefail
cd "$(dirname "$0")"
secs=${1:-300}
logs=${LOGDIR:-target/fuzz-logs}
bin=target/$(rustc -vV | sed -n 's/^host: //p')/release
mkdir -p "$logs"
for t in schema query search document search_structured; do
  mkdir -p "corpus/$t" "artifacts/$t"
  args=("corpus/$t")
  [ -d "seeds/$t" ] && args+=("seeds/$t")
  [ "$t" != search_structured ] && args+=(-dict=json.dict)
  "$bin/$t" "${args[@]}" -max_total_time="$secs" -rss_limit_mb=2048 -timeout=10 \
    -artifact_prefix="artifacts/$t/" -fork=1 -ignore_crashes=1 > "$logs/$t.log" 2>&1 &
done
wait
for t in schema query search document search_structured; do
  echo "$t: $(find "artifacts/$t" -type f | wc -l | tr -d ' ') inputs in artifacts/$t, $(grep -o 'cov: [0-9]*' "$logs/$t.log" | tail -1)"
done
