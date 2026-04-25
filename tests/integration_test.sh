#!/usr/bin/env bash
# Integration test for recording + playback pipeline
# Requires: jellyfin-dev container running, mock IPTV server on port 9090
set -eo pipefail

BASE_URL="${JELLYFIN_URL:-http://localhost:8096}"
API="$BASE_URL/Xtream"
STREAM_101=101
STREAM_102=102
PASS=0
FAIL=0
TIMERS_TO_CLEANUP=()

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${YELLOW}[TEST]${NC} $*"; }
pass() { echo -e "${GREEN}[PASS]${NC} $*"; PASS=$((PASS + 1)); }
fail() { echo -e "${RED}[FAIL]${NC} $*"; FAIL=$((FAIL + 1)); }

cleanup() {
    log "Cleaning up..."
    for tid in "${TIMERS_TO_CLEANUP[@]}"; do
        curl -s -X POST "$API/Test/StopRecording/$tid" >/dev/null 2>&1 || true
    done
    log "Done. Passed: $PASS, Failed: $FAIL"
    if [ "$FAIL" -gt 0 ]; then
        exit 1
    fi
}
trap cleanup EXIT

wait_for_jellyfin() {
    log "Waiting for Jellyfin to be ready..."
    for i in $(seq 1 30); do
        if curl -s "$BASE_URL/System/Info/Public" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    fail "Jellyfin not ready after 30s"
    exit 1
}

start_recording() {
    local stream_id=$1
    local response
    response=$(curl -s -X POST "$API/Test/StartRecording?streamId=$stream_id&durationMinutes=3")
    local timer_id
    timer_id=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin)['TimerId'])" 2>/dev/null)
    if [ -z "$timer_id" ]; then
        fail "Failed to start recording on stream $stream_id: $response"
        return 1
    fi
    TIMERS_TO_CLEANUP+=("$timer_id")
    echo "$timer_id"
}

get_active_recordings() {
    curl -s "$API/Test/ActiveRecordings" 2>/dev/null
}

# ===== TEST 1: Single recording starts and stream endpoint returns data =====
test_single_recording() {
    log "=== Test 1: Single recording starts and streams data ==="

    local timer_id
    timer_id=$(start_recording $STREAM_101) || return

    log "Started recording on stream $STREAM_101, timer: $timer_id"
    log "Waiting 15s for segments to accumulate..."
    sleep 15

    # Verify recording is active
    local active
    active=$(get_active_recordings)
    if echo "$active" | python3 -c "import sys,json; recs=json.load(sys.stdin); assert any(r['TimerId']=='$timer_id' for r in recs)" 2>/dev/null; then
        pass "Recording is active"
    else
        fail "Recording not found in active list: $active"
        return
    fi

    # Verify stream endpoint returns data
    local bytes
    bytes=$(curl -s -o /dev/null -w "%{size_download}" --max-time 3 "$API/Recordings/$timer_id/stream.ts" 2>/dev/null || true)
    if [ "${bytes:-0}" -gt 10000 ]; then
        pass "Stream endpoint returned ${bytes} bytes in 3s"
    else
        fail "Stream endpoint returned only ${bytes:-0} bytes (expected >10KB)"
        return
    fi

    # Verify stream continues to deliver data (two reads, second should have more)
    log "Testing continuous data delivery (two 5s reads)..."
    local bytes1 bytes2
    bytes1=$(curl -s -o /dev/null -w "%{size_download}" --max-time 5 "$API/Recordings/$timer_id/stream.ts" 2>/dev/null || true)
    sleep 10
    bytes2=$(curl -s -o /dev/null -w "%{size_download}" --max-time 5 "$API/Recordings/$timer_id/stream.ts" 2>/dev/null || true)

    if [ "${bytes2:-0}" -gt "${bytes1:-0}" ]; then
        pass "File is growing: first read ${bytes1} bytes, second read ${bytes2} bytes"
    else
        fail "File not growing: first read ${bytes1} bytes, second read ${bytes2} bytes"
    fi

    # Stop recording
    curl -s -X POST "$API/Test/StopRecording/$timer_id" >/dev/null 2>&1
    pass "Test 1 complete: single recording works"
}

# ===== TEST 2: Stream stays alive during data gaps (null packets) =====
test_stream_keepalive() {
    log "=== Test 2: Stream stays alive during data gaps ==="

    local timer_id
    timer_id=$(start_recording $STREAM_102) || return

    log "Started recording on stream $STREAM_102, timer: $timer_id"
    log "Waiting 15s for data..."
    sleep 15

    # Read for 30s — should get data even during multiplexer gaps
    log "Reading stream for 30s (should survive multiplexer gaps)..."
    local bytes
    bytes=$(curl -s -o /dev/null -w "%{size_download}" --max-time 30 "$API/Recordings/$timer_id/stream.ts" 2>/dev/null || true)

    if [ "${bytes:-0}" -gt 100000 ]; then
        pass "Stream alive for 30s, received ${bytes} bytes"
    else
        fail "Stream may have died: only ${bytes:-0} bytes in 30s"
    fi

    curl -s -X POST "$API/Test/StopRecording/$timer_id" >/dev/null 2>&1
    pass "Test 2 complete: stream keepalive works"
}

# ===== TEST 3: Two simultaneous recordings =====
test_two_recordings() {
    log "=== Test 3: Two simultaneous recordings ==="

    local timer1 timer2
    timer1=$(start_recording $STREAM_101) || return
    timer2=$(start_recording $STREAM_102) || return

    log "Started recordings: stream $STREAM_101 ($timer1), stream $STREAM_102 ($timer2)"
    log "Waiting 25s for both to accumulate data..."
    sleep 25

    # Verify both are active
    local active count
    active=$(get_active_recordings)
    count=$(echo "$active" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null)
    if [ "${count:-0}" -ge 2 ]; then
        pass "Both recordings are active ($count total)"
    else
        fail "Expected 2+ active recordings, got ${count:-0}: $active"
    fi

    # Verify both stream endpoints return data
    local b1 b2
    b1=$(curl -s -o /dev/null -w "%{size_download}" --max-time 5 "$API/Recordings/$timer1/stream.ts" 2>/dev/null || true)
    b2=$(curl -s -o /dev/null -w "%{size_download}" --max-time 5 "$API/Recordings/$timer2/stream.ts" 2>/dev/null || true)

    if [ "${b1:-0}" -gt 10000 ]; then
        pass "Recording 1 streaming: ${b1} bytes"
    else
        fail "Recording 1 not streaming: ${b1:-0} bytes"
    fi

    if [ "${b2:-0}" -gt 10000 ]; then
        pass "Recording 2 streaming: ${b2} bytes"
    else
        fail "Recording 2 not streaming: ${b2:-0} bytes"
    fi

    # Stop both
    curl -s -X POST "$API/Test/StopRecording/$timer1" >/dev/null 2>&1
    curl -s -X POST "$API/Test/StopRecording/$timer2" >/dev/null 2>&1
    pass "Test 3 complete: two recordings work"
}

# ===== TEST 4: Recording stops cleanly =====
test_recording_stops() {
    log "=== Test 4: Recording stops and stream ends ==="

    local timer_id
    timer_id=$(start_recording $STREAM_101) || return

    log "Started recording, waiting 15s..."
    sleep 15

    # Stop it
    curl -s -X POST "$API/Test/StopRecording/$timer_id" >/dev/null 2>&1
    log "Recording stopped, waiting 5s for cleanup..."
    sleep 5

    # Verify it's gone from active list
    local active
    active=$(get_active_recordings)
    if echo "$active" | python3 -c "import sys,json; recs=json.load(sys.stdin); assert not any(r['TimerId']=='$timer_id' for r in recs)" 2>/dev/null; then
        pass "Recording removed from active list"
    else
        fail "Recording still in active list after stop"
    fi

    # Stream endpoint should return 404 or finish quickly
    local code
    code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 5 "$API/Recordings/$timer_id/stream.ts" 2>/dev/null || true)
    if [ "$code" = "404" ] || [ "$code" = "200" ]; then
        pass "Stream endpoint returned $code after stop"
    else
        fail "Unexpected status $code after recording stop"
    fi

    pass "Test 4 complete: recording stops cleanly"
}

# ===== MAIN =====
log "Starting integration tests"
log "Base URL: $BASE_URL"
echo ""

wait_for_jellyfin

# Cancel any existing test recordings
existing=$(get_active_recordings 2>/dev/null || echo "[]")
log "Existing active recordings: $existing"
for tid in $(echo "$existing" | python3 -c "import sys,json; [print(r['TimerId']) for r in json.load(sys.stdin)]" 2>/dev/null); do
    log "Stopping leftover recording: $tid"
    curl -s -X POST "$API/Test/StopRecording/$tid" >/dev/null 2>&1 || true
done
sleep 5
echo ""

test_single_recording
echo ""
test_stream_keepalive
echo ""
test_two_recordings
echo ""
test_recording_stops
echo ""

log "============================="
log "Results: $PASS passed, $FAIL failed"
if [ "$FAIL" -gt 0 ]; then
    exit 1
else
    log "All tests passed!"
fi
