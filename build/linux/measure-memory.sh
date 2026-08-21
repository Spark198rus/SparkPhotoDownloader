#!/usr/bin/env bash
# Sample the memory footprint of a process and every child it spawns.
#
#   ./measure-memory.sh SparkPhotoDownloader
#   ./measure-memory.sh rapid-photo-downloader --interval 2 --duration 300 --csv rpd.csv
#
# Why a whole tree: Rapid Photo Downloader forks separate worker processes for scanning,
# thumbnailing and copying, so reading one PID's RSS understates it badly. Spark Photo
# Downloader is a single process with a thread pool. Summing the tree is the only way the
# two numbers mean the same thing.
#
# Why PSS and not RSS: RSS counts every shared page in full, so a Qt or .NET process is
# charged for library pages the whole system shares, and a multi-process program is charged
# for the same pages once per worker. PSS divides each shared page by the number of
# processes mapping it, which is the figure that actually answers "what does this cost me".
# PSS needs /proc/PID/smaps_rollup, present on any kernel from 4.14 on.

set -euo pipefail

NAME=""
INTERVAL=2
DURATION=0
CSV=""

usage() {
    sed -n '2,20p' "$0"
    exit "${1:-0}"
}

[[ $# -eq 0 ]] && usage 1
NAME="$1"; shift

while [[ $# -gt 0 ]]; do
    case "$1" in
        --interval) INTERVAL="$2"; shift 2 ;;
        --duration) DURATION="$2"; shift 2 ;;
        --csv)      CSV="$2"; shift 2 ;;
        -h|--help)  usage 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

# Every descendant of the given pids, breadth first.
collect_tree() {
    local pids=("$@") all=() queue=("$@")

    while [[ ${#queue[@]} -gt 0 ]]; do
        local pid="${queue[0]}"
        queue=("${queue[@]:1}")
        all+=("$pid")

        local children
        children=$(ps -o pid= --ppid "$pid" 2>/dev/null | tr -d ' ' || true)
        for child in $children; do
            [[ -n "$child" ]] && queue+=("$child")
        done
    done

    printf '%s\n' "${all[@]}"
}

# kB of Pss for one pid, falling back to Rss where smaps_rollup is unavailable.
pss_kb() {
    local pid="$1"
    if [[ -r "/proc/$pid/smaps_rollup" ]]; then
        awk '/^Pss:/ {sum += $2} END {print sum + 0}' "/proc/$pid/smaps_rollup"
    elif [[ -r "/proc/$pid/smaps" ]]; then
        awk '/^Pss:/ {sum += $2} END {print sum + 0}' "/proc/$pid/smaps"
    else
        awk '/^VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null || echo 0
    fi
}

rss_kb() {
    awk '/^VmRSS:/ {print $2 + 0}' "/proc/$1/status" 2>/dev/null || echo 0
}

roots=$(pgrep -f "$NAME" 2>/dev/null || true)
if [[ -z "$roots" ]]; then
    echo "no process matching '$NAME' is running" >&2
    exit 1
fi

echo "watching '$NAME' every ${INTERVAL}s (Ctrl-C to stop)"
printf '%8s  %5s  %12s  %12s  %s\n' "time" "procs" "PSS" "RSS" "peak PSS"
[[ -n "$CSV" ]] && echo "seconds,processes,pss_kb,rss_kb" > "$CSV"

started=$(date +%s)
peak=0

while true; do
    roots=$(pgrep -f "$NAME" 2>/dev/null || true)
    if [[ -z "$roots" ]]; then
        echo "process gone"
        break
    fi

    mapfile -t tree < <(collect_tree $roots | sort -u)

    total_pss=0
    total_rss=0
    for pid in "${tree[@]}"; do
        [[ -d "/proc/$pid" ]] || continue
        total_pss=$(( total_pss + $(pss_kb "$pid") ))
        total_rss=$(( total_rss + $(rss_kb "$pid") ))
    done

    (( total_pss > peak )) && peak=$total_pss
    elapsed=$(( $(date +%s) - started ))

    # awk rather than bc: bc is absent from many minimal installs, awk never is.
    awk -v s="$elapsed" -v n="${#tree[@]}" -v p="$total_pss" -v r="$total_rss" -v pk="$peak" \
        'BEGIN { printf "%7ds  %5d  %9.1f MB  %9.1f MB  %7.1f MB\n", s, n, p/1024, r/1024, pk/1024 }'

    [[ -n "$CSV" ]] && echo "$elapsed,${#tree[@]},$total_pss,$total_rss" >> "$CSV"

    if [[ "$DURATION" -gt 0 && "$elapsed" -ge "$DURATION" ]]; then
        break
    fi

    sleep "$INTERVAL"
done

echo
awk -v pk="$peak" 'BEGIN { printf "peak PSS: %.1f MB\n", pk/1024 }'
