#!/usr/bin/env bash
# speaker_capture.sh — Record audio from the default PipeWire output (speaker/sink monitor)
# into a WAV file using pw-record and pw-link.
#
# Usage examples:
#   ./speaker_capture.sh
#   ./speaker_capture.sh ~/temp/chrome_capture.wav
#   SINK=alsa_output.pci-0000_01_00.1.hdmi-stereo ./speaker_capture.sh
#
# How to test:
#   1. Start playing sound in Chrome (or any app).
#   2. Run this script.
#   3. Wait 5 seconds.
#   4. Press Ctrl+C.
#   5. Open the WAV in Audacity or play it with:  pw-play <output_file>

set -euo pipefail

# Requires bash 4.3+ (the -1 argument to printf '%(%Y%m%d_%H%M%S)T', which
# means "use the current system time", was introduced in bash 4.3).
# Most modern Linux distributions (Ubuntu 20.04+, Fedora 32+, etc.) ship
# bash 5.x, so this requirement is satisfied in typical PipeWire environments.

# ── configuration ────────────────────────────────────────────────────────────

# Number of 0.25 s polling intervals to wait for recorder ports (5 s total).
POLL_ATTEMPTS=20

# ── helpers ─────────────────────────────────────────────────────────────────

die() { echo "ERROR: $*" >&2; exit 1; }

# ── prerequisite checks ─────────────────────────────────────────────────────

command -v pw-record >/dev/null 2>&1 || die "pw-record not found. Install pipewire."
command -v pw-link   >/dev/null 2>&1 || die "pw-link not found. Install pipewire."

if ! command -v wpctl >/dev/null 2>&1 && ! command -v pw-cli >/dev/null 2>&1; then
    die "Neither wpctl nor pw-cli found. Install wireplumber or pipewire-utils for sink detection."
fi

# ── output filename ──────────────────────────────────────────────────────────

printf -v ts '%(%Y%m%d_%H%M%S)T' -1
OUTPUT="${1:-speaker_capture_${ts}.wav}"
unset ts

# ── sink detection ───────────────────────────────────────────────────────────

detect_sink_wpctl() {
    # wpctl status lists: * <id>. <node-name>  (the asterisk marks the default)
    # We need the node name, not the numeric id.
    local id name
    id=$(wpctl status 2>/dev/null \
        | awk '/Sinks:/,/^$/' \
        | grep '^[[:space:]]*\*' \
        | awk '{print $2}' \
        | tr -d '.')
    [ -z "$id" ] && return 1
    name=$(wpctl inspect "$id" 2>/dev/null \
        | grep 'node.name' \
        | sed 's/.*= "\(.*\)"/\1/')
    [ -z "$name" ] && return 1
    echo "$name"
}

detect_sink_pwcli() {
    # Fallback: pick the first sink node name from pw-link -o by looking for
    # a port named <node>:monitor_FL (present on all Audio/Sink nodes).
    pw-link -o 2>/dev/null \
        | grep ':monitor_FL$' \
        | head -1 \
        | sed 's/:monitor_FL//'
}

if [ -n "${SINK:-}" ]; then
    echo "Using sink from SINK environment variable: $SINK"
else
    echo "Detecting default PipeWire audio sink..."
    if command -v wpctl >/dev/null 2>&1; then
        SINK=$(detect_sink_wpctl) || SINK=""
    fi
    if [ -z "${SINK:-}" ]; then
        SINK=$(detect_sink_pwcli) || SINK=""
    fi
    if [ -z "${SINK:-}" ]; then
        cat >&2 <<'EOF'
ERROR: Could not automatically detect the default audio sink.

To find available sinks, run:
  wpctl status
  pw-link -o

Then re-run with the SINK variable:
  SINK=alsa_output.pci-0000_01_00.1.hdmi-stereo ./speaker_capture.sh
EOF
        exit 1
    fi
    echo "Detected sink: $SINK"
fi

# ── unique recorder node name ─────────────────────────────────────────────────

NODE_NAME="speaker_capture_$$"

# ── status summary ────────────────────────────────────────────────────────────

echo "Output file : $OUTPUT"
echo "Sink        : $SINK"
echo "Recorder    : $NODE_NAME"

# ── cleanup on Ctrl+C ─────────────────────────────────────────────────────────

PW_RECORD_PID=""

cleanup() {
    echo ""
    echo "Stopping recorder..."
    if [ -n "$PW_RECORD_PID" ] && kill -0 "$PW_RECORD_PID" 2>/dev/null; then
        kill -INT "$PW_RECORD_PID"
        wait "$PW_RECORD_PID" 2>/dev/null || true
    fi
    echo "Done. WAV file: $OUTPUT"
}

trap cleanup INT TERM

# ── start pw-record unconnected ───────────────────────────────────────────────

pw-record \
    --target 0 \
    --properties "node.name=$NODE_NAME,media.name=$NODE_NAME" \
    "$OUTPUT" &
PW_RECORD_PID=$!

# ── wait for recorder ports to appear ────────────────────────────────────────

echo "Waiting for recorder ports to appear..."
attempt=0
while [ "$attempt" -lt "$POLL_ATTEMPTS" ]; do
    if pw-link -i 2>/dev/null | grep -q "^${NODE_NAME}:"; then
        break
    fi
    sleep 0.25
    attempt=$(( attempt + 1 ))
done
if ! pw-link -i 2>/dev/null | grep -q "^${NODE_NAME}:"; then
    timeout_s=$(( POLL_ATTEMPTS / 4 ))
    die "Timed out after ${timeout_s}s waiting for recorder ports (node: $NODE_NAME). Is PipeWire running?"
fi
unset attempt

# ── create links ──────────────────────────────────────────────────────────────

echo "Linking ${SINK}:monitor_FL -> ${NODE_NAME}:input_FL"
pw-link "${SINK}:monitor_FL" "${NODE_NAME}:input_FL" \
    || die "Failed to link FL. Check that the sink name is correct with: pw-link -o"

echo "Linking ${SINK}:monitor_FR -> ${NODE_NAME}:input_FR"
pw-link "${SINK}:monitor_FR" "${NODE_NAME}:input_FR" \
    || die "Failed to link FR. Check that the sink name is correct with: pw-link -o"

echo "Recording... Press Ctrl+C to stop."

# ── wait for recorder to finish ───────────────────────────────────────────────

wait "$PW_RECORD_PID" || true
