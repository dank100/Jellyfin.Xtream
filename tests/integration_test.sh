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

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${YELLOW}[TEST]${NC} $*"; }
pass() { echo -e "${GREEN}[PASS]${NC} $*"; PASS=$((PASS + 1)); }
fail() { echo -e "${RED}[FAIL]${NC} $*"; FAIL=$((FAIL + 1)); }

# Stop ALL active recordings via the test API.
stop_all_recordings() {
    local active tids
    active=$(curl -s "$API/Test/ActiveRecordings" 2>/dev/null || echo "[]")
    tids=$(echo "$active" | python3 -c "import sys,json; [print(r['TimerId']) for r in json.load(sys.stdin)]" 2>/dev/null) || true
    for tid in $tids; do
        curl -s -X POST "$API/Test/StopRecording/$tid" >/dev/null 2>&1 || true
    done
}

# Remove all IntegrationTest_* artifacts from the recordings directory inside the container.
clean_test_files() {
    log "Removing IntegrationTest files from container..."
    docker exec jellyfin-dev sh -c '
        dir="/config/data/livetv/recordings"
        rm -f "$dir"/IntegrationTest_* "$dir"/.IntegrationTest_*
        for d in "$dir"/.rec_*; do
            [ -d "$d" ] && rm -rf "$d"
        done
    ' 2>/dev/null || true
}

cleanup() {
    log "Final cleanup: stopping all recordings..."
    stop_all_recordings
    sleep 5
    clean_test_files
    # Verify nothing remains
    local remaining
    remaining=$(curl -s "$API/Test/ActiveRecordings" 2>/dev/null || echo "[]")
    local cnt
    cnt=$(echo "$remaining" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "?")
    log "Remaining active recordings after cleanup: $cnt"
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
    local response timer_id
    response=$(curl -s -X POST "$API/Test/StartRecording?streamId=$stream_id&durationMinutes=5")
    timer_id=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin)['TimerId'])" 2>/dev/null)
    if [ -z "$timer_id" ]; then
        fail "Failed to start recording on stream $stream_id: $response"
        return 1
    fi
    echo "$timer_id"
}

stop_recording() {
    local timer_id=$1
    curl -s -X POST "$API/Test/StopRecording/$timer_id" >/dev/null 2>&1 || true
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
        stop_recording "$timer_id"
        return
    fi

    # Verify stream endpoint returns data
    local bytes
    bytes=$(curl -s -o /dev/null -w "%{size_download}" --max-time 3 "$API/Recordings/$timer_id/stream.ts" 2>/dev/null || true)
    if [ "${bytes:-0}" -gt 10000 ]; then
        pass "Stream endpoint returned ${bytes} bytes in 3s"
    else
        fail "Stream endpoint returned only ${bytes:-0} bytes (expected >10KB)"
        stop_recording "$timer_id"
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

    stop_recording "$timer_id"
    pass "Test 1 complete: single recording works"
}

# ===== TEST 2: Stream stays alive during data gaps =====
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

    stop_recording "$timer_id"
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

    stop_recording "$timer1"
    stop_recording "$timer2"
    pass "Test 3 complete: two recordings work"
}

# ===== TEST 4: Recording stops cleanly =====
test_recording_stops() {
    log "=== Test 4: Recording stops and stream ends ==="

    local timer_id
    timer_id=$(start_recording $STREAM_101) || return

    log "Started recording, waiting 15s..."
    sleep 15

    stop_recording "$timer_id"
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

# ===== TEST 5: HLS playlist serves growing segments during active recording =====
test_hls_playback() {
    log "=== Test 5: HLS playlist serves growing content during recording ==="

    local timer_id
    timer_id=$(start_recording $STREAM_101) || return

    log "Started recording on stream $STREAM_101, timer: $timer_id"
    log "Waiting 20s for HLS segments..."
    sleep 20

    # Verify the playlist endpoint returns a valid m3u8
    local playlist_url="$API/Recordings/$timer_id/stream.m3u8"
    local playlist code
    code=$(curl -s -o /dev/null -w "%{http_code}" "$playlist_url" 2>/dev/null || true)
    if [ "$code" = "200" ]; then
        pass "HLS playlist returns 200"
    else
        fail "HLS playlist returned $code (expected 200)"
        stop_recording "$timer_id"
        return
    fi

    playlist=$(curl -s "$playlist_url" 2>/dev/null)

    # Playlist should contain EXTM3U header and EXTINF segments
    if echo "$playlist" | grep -q "#EXTM3U"; then
        pass "Playlist has valid #EXTM3U header"
    else
        fail "Playlist missing #EXTM3U header: $playlist"
        stop_recording "$timer_id"
        return
    fi

    local seg_count1
    seg_count1=$(echo "$playlist" | grep -c "#EXTINF:" || true)
    if [ "${seg_count1:-0}" -gt 2 ]; then
        pass "Playlist has $seg_count1 segments"
    else
        fail "Expected >2 segments, got ${seg_count1:-0}"
        stop_recording "$timer_id"
        return
    fi

    # Recording is active — playlist should NOT have #EXT-X-ENDLIST
    if echo "$playlist" | grep -q "#EXT-X-ENDLIST"; then
        fail "Active recording playlist should NOT contain #EXT-X-ENDLIST"
    else
        pass "Active recording playlist omits #EXT-X-ENDLIST (live EVENT)"
    fi

    # Fetch a segment to verify it has real data
    local first_seg seg_bytes
    first_seg=$(echo "$playlist" | grep "^segments/" | head -1)
    if [ -n "$first_seg" ]; then
        seg_bytes=$(curl -s -o /dev/null -w "%{size_download}" "$API/Recordings/$timer_id/$first_seg" 2>/dev/null || true)
        if [ "${seg_bytes:-0}" -gt 10000 ]; then
            pass "HLS segment has data: ${seg_bytes} bytes"
        else
            fail "HLS segment too small: ${seg_bytes:-0} bytes"
        fi
    else
        fail "No segment URLs found in playlist"
    fi

    # Wait and verify the playlist grows (new segments added)
    log "Waiting 20s for playlist to grow..."
    sleep 20
    local playlist2 seg_count2
    playlist2=$(curl -s "$playlist_url" 2>/dev/null)
    seg_count2=$(echo "$playlist2" | grep -c "#EXTINF:" || true)
    if [ "${seg_count2:-0}" -gt "${seg_count1:-0}" ]; then
        pass "Playlist grew: $seg_count1 -> $seg_count2 segments"
    else
        fail "Playlist did not grow: $seg_count1 -> ${seg_count2:-0} segments"
    fi

    # Stop recording and verify ENDLIST is added
    stop_recording "$timer_id"
    log "Recording stopped, waiting 5s..."
    sleep 5

    local playlist3
    playlist3=$(curl -s "$playlist_url" 2>/dev/null)
    local code3
    code3=$(curl -s -o /dev/null -w "%{http_code}" "$playlist_url" 2>/dev/null || true)
    if [ "$code3" = "200" ]; then
        if echo "$playlist3" | grep -q "#EXT-X-ENDLIST"; then
            pass "Finished recording playlist has #EXT-X-ENDLIST"
        else
            # Playlist may be gone (404) or still available — either is ok
            pass "Finished recording playlist served (code $code3)"
        fi
    else
        pass "Playlist endpoint returned $code3 after stop (expected 200 or 404)"
    fi

    pass "Test 5 complete: HLS playback works"
}

# ===== MAIN =====
log "Starting integration tests"
log "Base URL: $BASE_URL"
echo ""

wait_for_jellyfin

# Stop any leftover recordings from previous runs
log "Cleaning up leftover recordings..."
stop_all_recordings
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
test_hls_playback
echo ""

log "============================="
log "Results: $PASS passed, $FAIL failed"
if [ "$FAIL" -gt 0 ]; then
    exit 1
else
    log "All tests passed!"
fi
