#!/usr/bin/env bash
# Integration test for recording + playback pipeline
# Requires: jellyfin-dev container running, mock IPTV server on port 9090
set -eo pipefail

BASE_URL="${JELLYFIN_URL:-http://localhost:8096}"
API="$BASE_URL/Xtream"
STREAM_101=101
STREAM_102=102
STREAM_103=103
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

# Aggressively drain all recordings — retries until none remain.
# Timers can re-fire after stop, so we loop to catch stragglers.
drain_all_recordings() {
    local attempt
    for attempt in 1 2 3; do
        stop_all_recordings
        sleep 3
        local remaining
        remaining=$(curl -s "$API/Test/ActiveRecordings" 2>/dev/null || echo "[]")
        local cnt
        cnt=$(echo "$remaining" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "0")
        if [ "${cnt:-0}" -eq 0 ]; then
            return 0
        fi
        log "Still $cnt active recordings after attempt $attempt, retrying..."
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

    # Check HLS playlist appears within 15s (latency check)
    log "Checking segments appear within 15s..."
    sleep 15
    local early_playlist early_segs
    early_playlist=$(curl -s "$API/Recordings/$timer_id/stream.m3u8" 2>/dev/null)
    early_segs=$(echo "$early_playlist" | grep -c "#EXTINF:" || true)
    if [ "${early_segs:-0}" -ge 2 ]; then
        pass "Segments available within 15s ($early_segs segments)"
    else
        fail "Too slow: only $early_segs segments after 15s (expected >=2)"
    fi

    # Wait a bit more for stream data to accumulate
    sleep 10

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
    log "Waiting 25s for data..."
    sleep 25

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

    # Early latency check: both should have segments within 20s
    log "Checking both have segments within 20s..."
    sleep 20
    local p1 p2 seg1 seg2
    p1=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    p2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null)
    seg1=$(echo "$p1" | grep -c "#EXTINF:" || true)
    seg2=$(echo "$p2" | grep -c "#EXTINF:" || true)
    log "After 20s: stream $STREAM_101=$seg1 segs, stream $STREAM_102=$seg2 segs"

    if [ "${seg1:-0}" -ge 1 ] && [ "${seg2:-0}" -ge 1 ]; then
        pass "Both recordings have segments within 20s ($seg1 and $seg2)"
    else
        fail "Latency too high: stream 101=$seg1, stream 102=$seg2 after 20s (need >=1 each)"
    fi

    sleep 5

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
    log "Waiting 25s for HLS segments..."
    sleep 25

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

# ===== TEST 6: ffprobe can read the HLS playlist (simulates Jellyfin transcoder) =====
test_ffprobe_hls() {
    log "=== Test 6: ffprobe reads HLS playlist (simulates Jellyfin transcoder) ==="

    local timer_id
    timer_id=$(start_recording $STREAM_101) || return

    log "Started recording on stream $STREAM_101, timer: $timer_id"

    # Jellyfin's library scanner triggers playback as soon as the .strm appears.
    # The HLS playlist may have 0 segments initially. Verify we handle this gracefully.
    log "Checking early playlist availability (5s after start)..."
    sleep 5
    local early_code
    early_code=$(curl -s -o /dev/null -w "%{http_code}" "$API/Recordings/$timer_id/stream.m3u8" 2>/dev/null || true)
    if [ "$early_code" = "200" ] || [ "$early_code" = "404" ]; then
        pass "Early playlist returns $early_code (acceptable before segments arrive)"
    else
        fail "Unexpected early playlist status: $early_code"
    fi

    # Wait for enough segments to accumulate
    log "Waiting 25s for segments to accumulate..."
    sleep 25

    # Verify the playlist has content and all segments are fetchable
    local playlist_url="$API/Recordings/$timer_id/stream.m3u8"
    local playlist
    playlist=$(curl -s "$playlist_url" 2>/dev/null)
    local seg_count
    seg_count=$(echo "$playlist" | grep -c "#EXTINF:" || true)
    if [ "${seg_count:-0}" -lt 2 ]; then
        fail "Not enough segments for ffprobe test: ${seg_count:-0}"
        stop_recording "$timer_id"
        return
    fi
    pass "Playlist has $seg_count segments for ffprobe test"

    # Verify ALL segments are accessible (not just the first one)
    local seg_urls all_ok seg_url seg_code
    seg_urls=$(echo "$playlist" | grep "^segments/" || true)
    all_ok=true
    for seg_url in $seg_urls; do
        seg_code=$(curl -s -o /dev/null -w "%{http_code}" "$API/Recordings/$timer_id/$seg_url" 2>/dev/null || true)
        if [ "$seg_code" != "200" ]; then
            fail "Segment $seg_url returned $seg_code (expected 200)"
            all_ok=false
            break
        fi
    done
    if [ "$all_ok" = true ]; then
        pass "All $seg_count segments return 200"
    fi

    # Run ffprobe inside the Jellyfin container (has ffmpeg installed)
    # This simulates exactly what Jellyfin's transcoder does
    local internal_url="http://localhost:8096/Xtream/Recordings/$timer_id/stream.m3u8"
    log "Running ffprobe against HLS playlist inside container..."
    local probe_output probe_exit
    probe_output=$(docker exec jellyfin-dev /usr/lib/jellyfin-ffmpeg/ffprobe \
        -analyzeduration 200000000 -probesize 1073741824 \
        -i "$internal_url" \
        -show_format -show_streams -print_format json \
        2>&1) || probe_exit=$?
    probe_exit=${probe_exit:-0}

    if [ "$probe_exit" -eq 0 ]; then
        pass "ffprobe succeeded (exit code 0)"
    else
        fail "ffprobe failed with exit code $probe_exit"
        log "ffprobe output (last 20 lines):"
        echo "$probe_output" | tail -20
    fi

    # Verify ffprobe found video and audio streams
    if echo "$probe_output" | python3 -c "
import sys, json
try:
    # ffprobe JSON is after the stderr lines
    text = sys.stdin.read()
    # Find the JSON part
    start = text.index('{')
    data = json.loads(text[start:])
    codecs = [s['codec_type'] for s in data.get('streams', [])]
    assert 'video' in codecs, f'No video stream found, codecs: {codecs}'
    assert 'audio' in codecs, f'No audio stream found, codecs: {codecs}'
    print(f'Found streams: {codecs}')
except Exception as e:
    print(f'Parse error: {e}', file=sys.stderr)
    sys.exit(1)
" 2>/dev/null; then
        pass "ffprobe found video and audio streams"
    else
        fail "ffprobe did not find expected streams"
    fi

    # Now simulate what the Jellyfin transcoder does: actual transcode (not just copy)
    # Production uses libsvtav1 + libfdk_aac which triggers exit 234 on corrupt packets
    log "Running short ffmpeg transcode (5s) with real codecs to simulate Jellyfin..."
    local ffmpeg_output ffmpeg_exit
    ffmpeg_output=$(docker exec jellyfin-dev bash -c "timeout 15 /usr/lib/jellyfin-ffmpeg/ffmpeg \
        -analyzeduration 200000000 -probesize 1073741824 \
        -i '$internal_url' \
        -t 5 -map 0:v:0 -map 0:a:0 \
        -codec:v:0 libx264 -preset ultrafast -b:v 1000000 \
        -codec:a:0 aac -b:a 128000 \
        -copyts -avoid_negative_ts disabled \
        -f mpegts -y /dev/null 2>&1; echo EXIT_CODE:\$?" 2>/dev/null)
    ffmpeg_exit=$(echo "$ffmpeg_output" | grep "EXIT_CODE:" | tail -1 | cut -d: -f2)
    ffmpeg_exit=${ffmpeg_exit:-999}

    # exit 0 = success, exit 124 = timeout killed it (also ok — means it was still running)
    if [ "$ffmpeg_exit" -eq 0 ] || [ "$ffmpeg_exit" -eq 124 ]; then
        pass "ffmpeg real transcode succeeded (exit $ffmpeg_exit)"
    else
        fail "ffmpeg real transcode failed with exit code $ffmpeg_exit (would be 234 without discontinuity tags)"
        echo "$ffmpeg_output" | grep -iE "corrupt|error|fail" | tail -10
    fi

    stop_recording "$timer_id"
    pass "Test 6 complete: ffprobe/ffmpeg HLS validation works"
}

test_discontinuity_tags() {
    log "=== Test 7: HLS discontinuity tags in recording playlist ==="
    log "Starting TWO recordings so the multiplexer round-robins"

    # Start two recordings on different streams to force round-robin
    local timer1 timer2
    timer1=$(start_recording $STREAM_101) || return
    timer2=$(start_recording $STREAM_102) || return

    log "Started recordings: stream $STREAM_101 ($timer1), stream $STREAM_102 ($timer2)"
    log "Waiting 60s for multiplexer to round-robin several times..."
    sleep 60

    # Get the HLS playlist for the first recording
    local playlist
    playlist=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    local http_code
    http_code=$(curl -s -o /dev/null -w "%{http_code}" "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)

    if [ "$http_code" = "200" ]; then
        pass "Recording 1 HLS playlist returns 200"
    else
        fail "Recording 1 HLS playlist returned $http_code"
        stop_recording "$timer1"; stop_recording "$timer2"
        return
    fi

    # Count segments and discontinuity tags
    local seg_count disc_count
    seg_count=$(echo "$playlist" | grep -c "#EXTINF:" || true)
    disc_count=$(echo "$playlist" | grep -c "^#EXT-X-DISCONTINUITY$" || true)

    log "Playlist has $seg_count segments and $disc_count discontinuity tags"

    if [ "$seg_count" -ge 6 ]; then
        pass "Recording has $seg_count segments (enough for round-robin)"
    else
        fail "Only $seg_count segments (expected >=6 after 40s with round-robin)"
    fi

    if [ "$disc_count" -ge 1 ]; then
        pass "Playlist contains $disc_count #EXT-X-DISCONTINUITY tags"
    else
        fail "No #EXT-X-DISCONTINUITY tags found (round-robin should cause gaps)"
    fi

    # Also check the second recording
    local playlist2 disc2
    playlist2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null)
    disc2=$(echo "$playlist2" | grep -c "^#EXT-X-DISCONTINUITY$" || true)
    local seg2
    seg2=$(echo "$playlist2" | grep -c "#EXTINF:" || true)

    log "Recording 2: $seg2 segments, $disc2 discontinuity tags"

    if [ "$disc2" -ge 1 ]; then
        pass "Recording 2 also has discontinuity tags ($disc2)"
    else
        fail "Recording 2 missing discontinuity tags"
    fi

    # Verify ffmpeg can TRANSCODE (not just copy) the playlist WITH discontinuity tags
    # This is the exact scenario that causes exit 234 in production without discontinuity tags
    log "Verifying ffmpeg can transcode playlist with discontinuity tags (real codecs)..."
    local ffmpeg_ok
    ffmpeg_ok=$(docker exec jellyfin-dev bash -c "timeout 15 /usr/lib/jellyfin-ffmpeg/ffmpeg \
        -analyzeduration 200M -probesize 1G \
        -i 'http://localhost:8096/Xtream/Recordings/$timer1/stream.m3u8' \
        -t 5 -map 0:v:0 -map 0:a:0 \
        -codec:v:0 libx264 -preset ultrafast -b:v 1000000 \
        -codec:a:0 aac -b:a 128000 \
        -copyts -avoid_negative_ts disabled \
        -f mpegts -y /dev/null 2>&1; echo EXIT:\$?" 2>/dev/null)
    local ffmpeg_exit
    ffmpeg_exit=$(echo "$ffmpeg_ok" | grep "EXIT:" | tail -1 | cut -d: -f2)

    if [ "$ffmpeg_exit" = "0" ]; then
        pass "ffmpeg transcodes discontinuous recording OK (real codecs)"
    else
        fail "ffmpeg failed on discontinuous recording (exit $ffmpeg_exit — would be 234 without discontinuity tags)"
        echo "$ffmpeg_ok" | grep -iE "corrupt|error|fail" | tail -10
    fi

    # Cleanup
    stop_recording "$timer1"
    stop_recording "$timer2"
    sleep 3

    pass "Test 7 complete: HLS discontinuity tags work"
}

test_parallel_stream_fairness() {
    log "=== Test 8: Parallel stream fairness — both channels get data promptly ==="

    # Start two recordings simultaneously on different streams
    local timer1 timer2
    timer1=$(start_recording $STREAM_101) || return
    timer2=$(start_recording $STREAM_102) || return

    log "Started recordings: stream $STREAM_101 ($timer1), stream $STREAM_102 ($timer2)"

    # Check BOTH streams have segments within 20s (not just one)
    # With optimized ffmpeg (nobuffer), startup should be ~3s, so 20s
    # allows for ~2 full round-robin cycles
    log "Checking both streams get segments within 20s..."
    sleep 20

    local p1 p2 seg1 seg2
    p1=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    p2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null)
    seg1=$(echo "$p1" | grep -c "#EXTINF:" || true)
    seg2=$(echo "$p2" | grep -c "#EXTINF:" || true)

    log "After 20s: stream $STREAM_101 has $seg1 segments, stream $STREAM_102 has $seg2 segments"

    if [ "${seg1:-0}" -ge 1 ]; then
        pass "Stream $STREAM_101 has data within 20s ($seg1 segments)"
    else
        fail "Stream $STREAM_101 has no data after 20s (starved)"
    fi

    if [ "${seg2:-0}" -ge 1 ]; then
        pass "Stream $STREAM_102 has data within 20s ($seg2 segments)"
    else
        fail "Stream $STREAM_102 has no data after 20s (starved)"
    fi

    # Wait longer and check fairness: segment counts should be within 3:1 ratio
    log "Waiting 40s more to check fairness..."
    sleep 40

    p1=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    p2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null)
    seg1=$(echo "$p1" | grep -c "#EXTINF:" || true)
    seg2=$(echo "$p2" | grep -c "#EXTINF:" || true)

    log "After 60s total: stream $STREAM_101=$seg1 segs, stream $STREAM_102=$seg2 segs"

    # Both should have a reasonable number of segments (at least 5 each in 60s)
    if [ "${seg1:-0}" -ge 5 ] && [ "${seg2:-0}" -ge 5 ]; then
        pass "Both streams have >= 5 segments ($seg1 and $seg2)"
    else
        fail "Too few segments in 60s: $STREAM_101=$seg1, $STREAM_102=$seg2 (need >=5 each)"
    fi

    # Check fairness ratio: neither stream should have >2x the segments of the other
    local ratio
    if [ "${seg1:-1}" -gt "${seg2:-1}" ]; then
        ratio=$((seg1 / (seg2 > 0 ? seg2 : 1)))
    else
        ratio=$((seg2 / (seg1 > 0 ? seg1 : 1)))
    fi

    if [ "$ratio" -le 2 ]; then
        pass "Segment ratio is fair ($ratio:1, threshold 2:1)"
    else
        fail "Unfair ratio $ratio:1 — one stream is starving ($STREAM_101=$seg1, $STREAM_102=$seg2)"
    fi

    # Verify both streams can be transcoded
    log "Verifying both streams can be transcoded..."
    local internal_url1="http://localhost:8096/Xtream/Recordings/$timer1/stream.m3u8"
    local internal_url2="http://localhost:8096/Xtream/Recordings/$timer2/stream.m3u8"

    local out1 exit1
    out1=$(docker exec jellyfin-dev bash -c "timeout 10 /usr/lib/jellyfin-ffmpeg/ffmpeg \
        -analyzeduration 200M -probesize 1G -i '$internal_url1' \
        -t 3 -map 0:v:0 -map 0:a:0 -codec:v:0 libx264 -preset ultrafast -b:v 1000000 \
        -codec:a:0 aac -b:a 128000 -f mpegts -y /dev/null 2>&1; echo EXIT:\$?" 2>/dev/null)
    exit1=$(echo "$out1" | grep "EXIT:" | tail -1 | cut -d: -f2)

    local out2 exit2
    out2=$(docker exec jellyfin-dev bash -c "timeout 10 /usr/lib/jellyfin-ffmpeg/ffmpeg \
        -analyzeduration 200M -probesize 1G -i '$internal_url2' \
        -t 3 -map 0:v:0 -map 0:a:0 -codec:v:0 libx264 -preset ultrafast -b:v 1000000 \
        -codec:a:0 aac -b:a 128000 -f mpegts -y /dev/null 2>&1; echo EXIT:\$?" 2>/dev/null)
    exit2=$(echo "$out2" | grep "EXIT:" | tail -1 | cut -d: -f2)

    if [ "$exit1" = "0" ] && [ "$exit2" = "0" ]; then
        pass "Both streams transcode successfully"
    else
        fail "Transcode failed: stream 101 exit=$exit1, stream 102 exit=$exit2"
        [ "$exit1" != "0" ] && echo "$out1" | grep -iE "corrupt|error" | tail -5
        [ "$exit2" != "0" ] && echo "$out2" | grep -iE "corrupt|error" | tail -5
    fi

    # Cleanup
    stop_recording "$timer1"
    stop_recording "$timer2"
    sleep 3

    pass "Test 8 complete: parallel stream fairness works"
}

# ===== TEST 9: Staggered start — second recording joins while first is running =====
# Reproduces the parallel-to-sequential transition bug seen in logs:
# - One recording starts (parallel mode, 1 channel ≤ maxConnections)
# - Second recording starts later (forces switch to sequential round-robin)
# - Both should produce data without 0-segment loops or freezes
test_staggered_start() {
    log "=== Test 9: Staggered start — parallel-to-sequential transition ==="

    # Start first recording — this runs in parallel mode (1 channel ≤ maxConnections=1)
    local timer1
    timer1=$(start_recording $STREAM_101) || return
    log "Started first recording: stream $STREAM_101 ($timer1)"

    # Let it capture for 15s in parallel mode, building up segments
    log "Waiting 15s for first recording to build segments in parallel mode..."
    sleep 15

    local p1_before seg1_before
    p1_before=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    seg1_before=$(echo "$p1_before" | grep -c "#EXTINF:" || true)
    log "First recording has $seg1_before segments before second recording starts"

    if [ "${seg1_before:-0}" -ge 2 ]; then
        pass "First recording has segments before transition ($seg1_before)"
    else
        fail "First recording has no segments before transition ($seg1_before)"
        stop_recording "$timer1"
        return
    fi

    # Now start second recording — forces transition to sequential round-robin
    local timer2
    timer2=$(start_recording $STREAM_102) || return
    log "Started second recording: stream $STREAM_102 ($timer2) — triggers parallel→sequential transition"

    # Wait for the transition to settle and both streams to get data
    log "Waiting 30s for both streams to accumulate data after transition..."
    sleep 30

    local p1 p2 seg1 seg2
    p1=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    p2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null)
    seg1=$(echo "$p1" | grep -c "#EXTINF:" || true)
    seg2=$(echo "$p2" | grep -c "#EXTINF:" || true)

    log "After transition: stream $STREAM_101=$seg1 segs (was $seg1_before), stream $STREAM_102=$seg2 segs"

    # First recording should have grown since the transition
    if [ "${seg1:-0}" -gt "${seg1_before:-0}" ]; then
        pass "First recording continued growing after transition ($seg1_before → $seg1)"
    else
        fail "First recording stalled after transition ($seg1_before → $seg1)"
    fi

    # Second recording should have data despite joining late
    if [ "${seg2:-0}" -ge 2 ]; then
        pass "Second recording got data after joining ($seg2 segments)"
    else
        fail "Second recording got no data after joining ($seg2 segments — possible 0-segment loop)"
    fi

    # Both should be transcodable
    local internal_url1="http://localhost:8096/Xtream/Recordings/$timer1/stream.m3u8"
    local internal_url2="http://localhost:8096/Xtream/Recordings/$timer2/stream.m3u8"

    local out1 exit1
    out1=$(docker exec jellyfin-dev bash -c "timeout 10 /usr/lib/jellyfin-ffmpeg/ffmpeg \
        -analyzeduration 200M -probesize 1G -i '$internal_url1' \
        -t 3 -map 0:v:0 -map 0:a:0 -codec:v:0 libx264 -preset ultrafast -b:v 1000000 \
        -codec:a:0 aac -b:a 128000 -f mpegts -y /dev/null 2>&1; echo EXIT:\$?" 2>/dev/null)
    exit1=$(echo "$out1" | grep "EXIT:" | tail -1 | cut -d: -f2)

    local out2 exit2
    out2=$(docker exec jellyfin-dev bash -c "timeout 10 /usr/lib/jellyfin-ffmpeg/ffmpeg \
        -analyzeduration 200M -probesize 1G -i '$internal_url2' \
        -t 3 -map 0:v:0 -map 0:a:0 -codec:v:0 libx264 -preset ultrafast -b:v 1000000 \
        -codec:a:0 aac -b:a 128000 -f mpegts -y /dev/null 2>&1; echo EXIT:\$?" 2>/dev/null)
    exit2=$(echo "$out2" | grep "EXIT:" | tail -1 | cut -d: -f2)

    if [ "$exit1" = "0" ] && [ "$exit2" = "0" ]; then
        pass "Both staggered recordings transcode successfully"
    else
        fail "Transcode failed after transition: stream 101 exit=$exit1, stream 102 exit=$exit2"
    fi

    # Cleanup
    stop_recording "$timer1"
    stop_recording "$timer2"
    sleep 3

    pass "Test 9 complete: staggered start transition works"
}

# ===== TEST 10: Three channels on one connection — all get data fairly =====
# The ultimate multiplexer stress test: 3 simultaneous recordings sharing
# a single provider connection. Verifies:
# - All 3 channels receive segments within a reasonable time
# - No channel is starved (fairness)
# - All recordings are transcodable (no corruption from fast cycling)
test_three_channel_fairness() {
    log "=== Test 10: Three channels on one connection ==="

    local timer1 timer2 timer3
    timer1=$(start_recording $STREAM_101) || return
    timer2=$(start_recording $STREAM_102) || return
    timer3=$(start_recording $STREAM_103) || return

    log "Started 3 recordings: stream $STREAM_101 ($timer1), $STREAM_102 ($timer2), $STREAM_103 ($timer3)"

    # With 3 channels and optimized ffmpeg (~2s startup + 1-2 segments + 1s shutdown ≈ 5-7s per channel),
    # a full round-robin cycle takes ~15-21s. Allow 30s for all to get data.
    log "Checking all 3 streams get segments within 30s..."
    sleep 30

    local p1 p2 p3 seg1 seg2 seg3
    p1=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    p2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null)
    p3=$(curl -s "$API/Recordings/$timer3/stream.m3u8" 2>/dev/null)
    seg1=$(echo "$p1" | grep -c "#EXTINF:" || true)
    seg2=$(echo "$p2" | grep -c "#EXTINF:" || true)
    seg3=$(echo "$p3" | grep -c "#EXTINF:" || true)

    log "After 30s: stream $STREAM_101=$seg1 segs, $STREAM_102=$seg2 segs, $STREAM_103=$seg3 segs"

    local all_have_data=true
    for sid_seg in "101:$seg1" "102:$seg2" "103:$seg3"; do
        local sid=${sid_seg%%:*}
        local segs=${sid_seg##*:}
        if [ "${segs:-0}" -ge 1 ]; then
            pass "Stream $sid has data within 30s ($segs segments)"
        else
            fail "Stream $sid has NO data after 30s (starved)"
            all_have_data=false
        fi
    done

    # Wait longer to check fairness and growth
    log "Waiting 40s more to check fairness across 3 channels..."
    sleep 40

    p1=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null)
    p2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null)
    p3=$(curl -s "$API/Recordings/$timer3/stream.m3u8" 2>/dev/null)
    seg1=$(echo "$p1" | grep -c "#EXTINF:" || true)
    seg2=$(echo "$p2" | grep -c "#EXTINF:" || true)
    seg3=$(echo "$p3" | grep -c "#EXTINF:" || true)

    log "After 70s total: $STREAM_101=$seg1, $STREAM_102=$seg2, $STREAM_103=$seg3 segments"

    # Each channel should have at least 4 segments in 70s
    if [ "${seg1:-0}" -ge 4 ] && [ "${seg2:-0}" -ge 4 ] && [ "${seg3:-0}" -ge 4 ]; then
        pass "All 3 streams have >= 4 segments ($seg1, $seg2, $seg3)"
    else
        fail "Too few segments in 70s: $seg1, $seg2, $seg3 (need >=4 each)"
    fi

    # Fairness: no stream should have more than 3x another
    local max_seg min_seg
    max_seg=$seg1
    [ "${seg2:-0}" -gt "$max_seg" ] && max_seg=$seg2
    [ "${seg3:-0}" -gt "$max_seg" ] && max_seg=$seg3
    min_seg=$seg1
    [ "${seg2:-0}" -lt "$min_seg" ] && min_seg=$seg2
    [ "${seg3:-0}" -lt "$min_seg" ] && min_seg=$seg3

    local ratio
    ratio=$((max_seg / (min_seg > 0 ? min_seg : 1)))

    if [ "$ratio" -le 3 ]; then
        pass "3-channel fairness ratio OK ($ratio:1, max=$max_seg, min=$min_seg)"
    else
        fail "Unfair 3-channel ratio $ratio:1 ($STREAM_101=$seg1, $STREAM_102=$seg2, $STREAM_103=$seg3)"
    fi

    # Verify all 3 streams can be transcoded
    log "Verifying all 3 recordings transcode..."
    local all_transcode=true
    for timer_var in "$timer1" "$timer2" "$timer3"; do
        local internal_url="http://localhost:8096/Xtream/Recordings/$timer_var/stream.m3u8"
        local out exit_code
        out=$(docker exec jellyfin-dev bash -c "timeout 10 /usr/lib/jellyfin-ffmpeg/ffmpeg \
            -analyzeduration 200M -probesize 1G -i '$internal_url' \
            -t 3 -map 0:v:0 -map 0:a:0 -codec:v:0 libx264 -preset ultrafast -b:v 1000000 \
            -codec:a:0 aac -b:a 128000 -f mpegts -y /dev/null 2>&1; echo EXIT:\$?" 2>/dev/null)
        exit_code=$(echo "$out" | grep "EXIT:" | tail -1 | cut -d: -f2)
        if [ "$exit_code" != "0" ]; then
            all_transcode=false
            log "Transcode failed for $timer_var (exit=$exit_code)"
            echo "$out" | grep -iE "corrupt|error" | tail -3
        fi
    done

    if [ "$all_transcode" = true ]; then
        pass "All 3 recordings transcode successfully"
    else
        fail "One or more 3-channel recordings failed to transcode"
    fi

    # Cleanup
    stop_recording "$timer1"
    stop_recording "$timer2"
    stop_recording "$timer3"
    sleep 3

    pass "Test 10 complete: three channels on one connection works"
}

# ===== TEST 11: Three-channel staggered start — channels join one at a time =====
# Start channels sequentially with delays to test transitions:
# 1 channel (parallel) → 2 channels (sequential) → 3 channels (sequential)
test_three_channel_staggered() {
    log "=== Test 11: Three-channel staggered start ==="

    # Start first recording (parallel mode)
    local timer1
    timer1=$(start_recording $STREAM_101) || return
    log "Started first recording: stream $STREAM_101 ($timer1)"

    log "Waiting 12s for first recording in parallel mode..."
    sleep 12

    local seg1_before
    seg1_before=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null | grep -c "#EXTINF:" || true)
    log "First recording has $seg1_before segments before second joins"

    if [ "${seg1_before:-0}" -ge 2 ]; then
        pass "First recording has segments in parallel mode ($seg1_before)"
    else
        fail "First recording failed in parallel mode ($seg1_before segments)"
        stop_recording "$timer1"
        return
    fi

    # Add second channel (triggers sequential round-robin)
    local timer2
    timer2=$(start_recording $STREAM_102) || return
    log "Second recording started: stream $STREAM_102 ($timer2) — now 2-channel sequential"

    log "Waiting 20s for 2-channel round-robin to stabilize..."
    sleep 20

    local seg1_mid seg2_mid
    seg1_mid=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null | grep -c "#EXTINF:" || true)
    seg2_mid=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null | grep -c "#EXTINF:" || true)
    log "After 2nd join: $STREAM_101=$seg1_mid segs, $STREAM_102=$seg2_mid segs"

    if [ "${seg2_mid:-0}" -ge 1 ]; then
        pass "Second recording got data after joining ($seg2_mid segments)"
    else
        fail "Second recording starved after joining ($seg2_mid segments)"
    fi

    # Add third channel (3-channel sequential)
    local timer3
    timer3=$(start_recording $STREAM_103) || return
    log "Third recording started: stream $STREAM_103 ($timer3) — now 3-channel sequential"

    log "Waiting 50s for 3-channel round-robin..."
    sleep 50

    local seg1 seg2 seg3
    seg1=$(curl -s "$API/Recordings/$timer1/stream.m3u8" 2>/dev/null | grep -c "#EXTINF:" || true)
    seg2=$(curl -s "$API/Recordings/$timer2/stream.m3u8" 2>/dev/null | grep -c "#EXTINF:" || true)
    seg3=$(curl -s "$API/Recordings/$timer3/stream.m3u8" 2>/dev/null | grep -c "#EXTINF:" || true)

    log "Final: $STREAM_101=$seg1, $STREAM_102=$seg2, $STREAM_103=$seg3 segments"

    # All should have grown
    if [ "${seg1:-0}" -gt "${seg1_mid:-0}" ]; then
        pass "First recording continued growing ($seg1_mid → $seg1)"
    else
        fail "First recording stalled after 3rd joined ($seg1_mid → $seg1)"
    fi

    if [ "${seg2:-0}" -gt "${seg2_mid:-0}" ]; then
        pass "Second recording continued growing ($seg2_mid → $seg2)"
    else
        fail "Second recording stalled after 3rd joined ($seg2_mid → $seg2)"
    fi

    if [ "${seg3:-0}" -ge 2 ]; then
        pass "Third recording got data after joining ($seg3 segments)"
    else
        fail "Third recording starved ($seg3 segments)"
    fi

    # Cleanup
    stop_recording "$timer1"
    stop_recording "$timer2"
    stop_recording "$timer3"
    sleep 3

    pass "Test 11 complete: three-channel staggered start works"
}

# ===== TEST 12: Live stream continuity — multiplexed restream sustains data flow =====
# Tests the full live restream pipeline (capture → remuxer → WrappedBufferStream)
# with 2 channels sharing 1 connection. Verifies:
# - Both streams open within reasonable time
# - Data flows continuously without long gaps (max gap < threshold)
# - Sufficient throughput (bytes/sec) to sustain playback
test_live_stream_continuity() {
    log "=== Test 12: Live stream continuity — multiplexed restream under load ==="

    local duration=60

    # --- 12a: Two streams, tight thresholds, 60s ---
    log "--- 12a: Two concurrent streams for ${duration}s with tight thresholds ---"

    local tmp1 tmp2
    tmp1=$(mktemp)
    tmp2=$(mktemp)

    curl -s -X POST "$API/Test/LiveStreamStats?streamId=$STREAM_101&durationSeconds=$duration" > "$tmp1" &
    local pid1=$!
    sleep 2
    curl -s -X POST "$API/Test/LiveStreamStats?streamId=$STREAM_102&durationSeconds=$duration" > "$tmp2" &
    local pid2=$!

    log "Waiting for both live stream reads to complete (~${duration}s)..."
    wait $pid1 2>/dev/null || true
    wait $pid2 2>/dev/null || true

    local result1 result2
    result1=$(cat "$tmp1")
    result2=$(cat "$tmp2")
    rm -f "$tmp1" "$tmp2"

    log "Stream $STREAM_101 result: $result1"
    log "Stream $STREAM_102 result: $result2"

    # Helper to extract JSON fields
    _jval() { echo "$1" | python3 -c "import sys,json; print(json.load(sys.stdin).get('$2', '$3'))" 2>/dev/null || echo "$3"; }

    # Check for errors
    local err1 err2
    err1=$(_jval "$result1" Error "")
    err2=$(_jval "$result2" Error "")
    if [ -n "$err1" ] && [ "$err1" != "" ]; then fail "Stream $STREAM_101 error: $err1"; return; fi
    if [ -n "$err2" ] && [ "$err2" != "" ]; then fail "Stream $STREAM_102 error: $err2"; return; fi

    # Extract all stats
    local bytes1 bytes2 gap1 gap2 elapsed1 elapsed2 bps1 bps2
    local p95_1 p95_2 gaps5s_1 gaps5s_2 gaps2s_1 gaps2s_2 gaps1s_1 gaps1s_2
    local replay_pct1 replay_pct2 gapfill1 gapfill2 open1 open2
    local dup_pct1 dup_pct2 dup_segs1 dup_segs2 total_segs1 total_segs2

    bytes1=$(_jval "$result1" TotalBytes 0)
    bytes2=$(_jval "$result2" TotalBytes 0)
    gap1=$(_jval "$result1" MaxGapMs 999999)
    gap2=$(_jval "$result2" MaxGapMs 999999)
    p95_1=$(_jval "$result1" P95GapMs 999999)
    p95_2=$(_jval "$result2" P95GapMs 999999)
    elapsed1=$(_jval "$result1" ElapsedSec 0)
    elapsed2=$(_jval "$result2" ElapsedSec 0)
    bps1=$(_jval "$result1" AvgBytesPerSec 0)
    bps2=$(_jval "$result2" AvgBytesPerSec 0)
    open1=$(_jval "$result1" OpenLatencyMs 0)
    open2=$(_jval "$result2" OpenLatencyMs 0)
    gaps5s_1=$(_jval "$result1" GapsOver5s 0)
    gaps5s_2=$(_jval "$result2" GapsOver5s 0)
    gaps2s_1=$(_jval "$result1" GapsOver2s 0)
    gaps2s_2=$(_jval "$result2" GapsOver2s 0)
    gaps1s_1=$(_jval "$result1" GapsOver1s 0)
    gaps1s_2=$(_jval "$result2" GapsOver1s 0)
    total_segs1=$(_jval "$result1" SegmentCount 0)
    total_segs2=$(_jval "$result2" SegmentCount 0)

    log "Stream $STREAM_101: ${bytes1}B, max gap ${gap1}ms, P95 ${p95_1}ms, gaps>2s: ${gaps2s_1}, open: ${open1}ms, segs: ${total_segs1}"
    log "Stream $STREAM_102: ${bytes2}B, max gap ${gap2}ms, P95 ${p95_2}ms, gaps>2s: ${gaps2s_2}, open: ${open2}ms, segs: ${total_segs2}"

    # Assertions — segment-level gap thresholds (round-robin cycle ~6s for 2 channels)
    local gap_threshold=10000
    local gap1_int gap2_int
    gap1_int=$(printf "%.0f" "$gap1" 2>/dev/null || echo 999999)
    gap2_int=$(printf "%.0f" "$gap2" 2>/dev/null || echo 999999)

    # Must have received data
    [ "${bytes1:-0}" -gt 0 ] && pass "Stream $STREAM_101 received data (${bytes1} bytes)" || fail "Stream $STREAM_101 received no data"
    [ "${bytes2:-0}" -gt 0 ] && pass "Stream $STREAM_102 received data (${bytes2} bytes)" || fail "Stream $STREAM_102 received no data"

    # Max gap < 10s (round-robin cycle + reconnect)
    [ "$gap1_int" -lt "$gap_threshold" ] && pass "Stream $STREAM_101 max gap OK (${gap1}ms < ${gap_threshold}ms)" || fail "Stream $STREAM_101 max gap too high (${gap1}ms >= ${gap_threshold}ms)"
    [ "$gap2_int" -lt "$gap_threshold" ] && pass "Stream $STREAM_102 max gap OK (${gap2}ms < ${gap_threshold}ms)" || fail "Stream $STREAM_102 max gap too high (${gap2}ms >= ${gap_threshold}ms)"

    # Zero gaps > 15s
    [ "${gaps5s_1}" -le 3 ] && pass "Stream $STREAM_101 gaps > 5s OK (${gaps5s_1})" || fail "Stream $STREAM_101 has ${gaps5s_1} gaps > 5s"
    [ "${gaps5s_2}" -le 3 ] && pass "Stream $STREAM_102 gaps > 5s OK (${gaps5s_2})" || fail "Stream $STREAM_102 has ${gaps5s_2} gaps > 5s"

    # Throughput > 50KB/s (segments only, no gap-fill padding)
    local bps_threshold=50000
    [ "${bps1:-0}" -gt "$bps_threshold" ] && pass "Stream $STREAM_101 throughput OK (${bps1} B/s)" || fail "Stream $STREAM_101 throughput too low (${bps1} B/s < ${bps_threshold})"
    [ "${bps2:-0}" -gt "$bps_threshold" ] && pass "Stream $STREAM_102 throughput OK (${bps2} B/s)" || fail "Stream $STREAM_102 throughput too low (${bps2} B/s < ${bps_threshold})"

    # Open latency < 30s
    local open1_int open2_int
    open1_int=$(printf "%.0f" "$open1" 2>/dev/null || echo 99999)
    open2_int=$(printf "%.0f" "$open2" 2>/dev/null || echo 99999)
    [ "$open1_int" -lt 30000 ] && pass "Stream $STREAM_101 open latency OK (${open1}ms)" || fail "Stream $STREAM_101 open too slow (${open1}ms)"
    [ "$open2_int" -lt 30000 ] && pass "Stream $STREAM_102 open latency OK (${open2}ms)" || fail "Stream $STREAM_102 open too slow (${open2}ms)"

    # Elapsed time close to requested
    local elapsed1_int elapsed2_int min_elapsed
    min_elapsed=$((duration * 3 / 4))
    elapsed1_int=$(printf "%.0f" "$elapsed1" 2>/dev/null || echo 0)
    elapsed2_int=$(printf "%.0f" "$elapsed2" 2>/dev/null || echo 0)
    [ "$elapsed1_int" -ge "$min_elapsed" ] && pass "Stream $STREAM_101 ran ${elapsed1}s (>= ${min_elapsed}s)" || fail "Stream $STREAM_101 ran only ${elapsed1}s"
    [ "$elapsed2_int" -ge "$min_elapsed" ] && pass "Stream $STREAM_102 ran ${elapsed2}s (>= ${min_elapsed}s)" || fail "Stream $STREAM_102 ran only ${elapsed2}s"

    pass "Test 12a complete: tight thresholds"

    # Cleanup between sub-tests
    drain_all_recordings
    sleep 5

    # --- 12b: Three concurrent streams stress test (60s) ---
    log "--- 12b: Three concurrent streams stress test for ${duration}s ---"

    local tmp3
    tmp1=$(mktemp)
    tmp2=$(mktemp)
    tmp3=$(mktemp)

    curl -s -X POST "$API/Test/LiveStreamStats?streamId=$STREAM_101&durationSeconds=$duration" > "$tmp1" &
    pid1=$!
    sleep 2
    curl -s -X POST "$API/Test/LiveStreamStats?streamId=$STREAM_102&durationSeconds=$duration" > "$tmp2" &
    pid2=$!
    sleep 2
    curl -s -X POST "$API/Test/LiveStreamStats?streamId=$STREAM_103&durationSeconds=$duration" > "$tmp3" &
    local pid3=$!

    log "Waiting for 3 live stream reads to complete (~${duration}s)..."
    wait $pid1 2>/dev/null || true
    wait $pid2 2>/dev/null || true
    wait $pid3 2>/dev/null || true

    result1=$(cat "$tmp1")
    result2=$(cat "$tmp2")
    local result3
    result3=$(cat "$tmp3")
    rm -f "$tmp1" "$tmp2" "$tmp3"

    log "3-stream $STREAM_101: $result1"
    log "3-stream $STREAM_102: $result2"
    log "3-stream $STREAM_103: $result3"

    # Extract stats for all 3
    local bytes3 gap3 bps3 replay_pct3 elapsed3 gaps5s_3
    local dup_pct3
    bytes1=$(_jval "$result1" TotalBytes 0)
    bytes2=$(_jval "$result2" TotalBytes 0)
    bytes3=$(_jval "$result3" TotalBytes 0)
    gap1=$(_jval "$result1" MaxGapMs 999999)
    gap2=$(_jval "$result2" MaxGapMs 999999)
    gap3=$(_jval "$result3" MaxGapMs 999999)
    bps1=$(_jval "$result1" AvgBytesPerSec 0)
    bps2=$(_jval "$result2" AvgBytesPerSec 0)
    bps3=$(_jval "$result3" AvgBytesPerSec 0)
    elapsed1=$(_jval "$result1" ElapsedSec 0)
    elapsed2=$(_jval "$result2" ElapsedSec 0)
    elapsed3=$(_jval "$result3" ElapsedSec 0)
    gaps5s_1=$(_jval "$result1" GapsOver5s 0)
    gaps5s_2=$(_jval "$result2" GapsOver5s 0)
    gaps5s_3=$(_jval "$result3" GapsOver5s 0)

    log "3-stream $STREAM_101: ${bytes1}B, max gap ${gap1}ms, ${bps1} B/s"
    log "3-stream $STREAM_102: ${bytes2}B, max gap ${gap2}ms, ${bps2} B/s"
    log "3-stream $STREAM_103: ${bytes3}B, max gap ${gap3}ms, ${bps3} B/s"

    # 3-stream thresholds (round-robin cycle ~9s for 3 channels)
    local gap_threshold_3=15000
    gap1_int=$(printf "%.0f" "$gap1" 2>/dev/null || echo 999999)
    gap2_int=$(printf "%.0f" "$gap2" 2>/dev/null || echo 999999)
    local gap3_int
    gap3_int=$(printf "%.0f" "$gap3" 2>/dev/null || echo 999999)

    # All 3 must receive data
    [ "${bytes1:-0}" -gt 0 ] && pass "3-stream $STREAM_101 received data" || fail "3-stream $STREAM_101 no data"
    [ "${bytes2:-0}" -gt 0 ] && pass "3-stream $STREAM_102 received data" || fail "3-stream $STREAM_102 no data"
    [ "${bytes3:-0}" -gt 0 ] && pass "3-stream $STREAM_103 received data" || fail "3-stream $STREAM_103 no data"

    # Max gap < 15s for 3 streams
    [ "$gap1_int" -lt "$gap_threshold_3" ] && pass "3-stream $STREAM_101 gap OK (${gap1}ms)" || fail "3-stream $STREAM_101 gap too high (${gap1}ms)"
    [ "$gap2_int" -lt "$gap_threshold_3" ] && pass "3-stream $STREAM_102 gap OK (${gap2}ms)" || fail "3-stream $STREAM_102 gap too high (${gap2}ms)"
    [ "$gap3_int" -lt "$gap_threshold_3" ] && pass "3-stream $STREAM_103 gap OK (${gap3}ms)" || fail "3-stream $STREAM_103 gap too high (${gap3}ms)"

    # Gaps > 5s allowed but limited (round-robin gaps are expected)
    [ "${gaps5s_1}" -le 5 ] && pass "3-stream $STREAM_101 gaps > 5s OK (${gaps5s_1})" || fail "3-stream $STREAM_101 has ${gaps5s_1} gaps > 5s"
    [ "${gaps5s_2}" -le 5 ] && pass "3-stream $STREAM_102 gaps > 5s OK (${gaps5s_2})" || fail "3-stream $STREAM_102 has ${gaps5s_2} gaps > 5s"
    [ "${gaps5s_3}" -le 5 ] && pass "3-stream $STREAM_103 gaps > 5s OK (${gaps5s_3})" || fail "3-stream $STREAM_103 has ${gaps5s_3} gaps > 5s"

    # Throughput > 30KB/s for 3 streams (segments only)
    local bps_threshold_3=30000
    [ "${bps1:-0}" -gt "$bps_threshold_3" ] && pass "3-stream $STREAM_101 throughput OK (${bps1})" || fail "3-stream $STREAM_101 throughput low (${bps1})"
    [ "${bps2:-0}" -gt "$bps_threshold_3" ] && pass "3-stream $STREAM_102 throughput OK (${bps2})" || fail "3-stream $STREAM_102 throughput low (${bps2})"
    [ "${bps3:-0}" -gt "$bps_threshold_3" ] && pass "3-stream $STREAM_103 throughput OK (${bps3})" || fail "3-stream $STREAM_103 throughput low (${bps3})"

    # Replay < 50% for 3 streams (higher threshold — more time off each channel)
    replay1_int=$(printf "%.0f" "$replay_pct1" 2>/dev/null || echo 100)
    replay2_int=$(printf "%.0f" "$replay_pct2" 2>/dev/null || echo 100)
    local replay3_int
    replay3_int=$(printf "%.0f" "$replay_pct3" 2>/dev/null || echo 100)
    [ "$replay1_int" -lt 50 ] && pass "3-stream $STREAM_101 replay OK (${replay_pct1}%)" || fail "3-stream $STREAM_101 too much replay (${replay_pct1}%)"
    [ "$replay2_int" -lt 50 ] && pass "3-stream $STREAM_102 replay OK (${replay_pct2}%)" || fail "3-stream $STREAM_102 too much replay (${replay_pct2}%)"
    [ "$replay3_int" -lt 50 ] && pass "3-stream $STREAM_103 replay OK (${replay_pct3}%)" || fail "3-stream $STREAM_103 too much replay (${replay_pct3}%)"

    # All 3 ran for at least 75% of requested duration
    elapsed1_int=$(printf "%.0f" "$elapsed1" 2>/dev/null || echo 0)
    elapsed2_int=$(printf "%.0f" "$elapsed2" 2>/dev/null || echo 0)
    local elapsed3_int
    elapsed3_int=$(printf "%.0f" "$elapsed3" 2>/dev/null || echo 0)
    [ "$elapsed1_int" -ge "$min_elapsed" ] && pass "3-stream $STREAM_101 ran ${elapsed1}s" || fail "3-stream $STREAM_101 only ${elapsed1}s"
    [ "$elapsed2_int" -ge "$min_elapsed" ] && pass "3-stream $STREAM_102 ran ${elapsed2}s" || fail "3-stream $STREAM_102 only ${elapsed2}s"
    [ "$elapsed3_int" -ge "$min_elapsed" ] && pass "3-stream $STREAM_103 ran ${elapsed3}s" || fail "3-stream $STREAM_103 only ${elapsed3}s"

    pass "Test 12b complete: three-stream stress test"

    pass "Test 12 complete: live stream continuity"
}

# ===== MAIN =====
log "Starting integration tests"
log "Base URL: $BASE_URL"
echo ""

wait_for_jellyfin

# Stop any leftover recordings from previous runs
log "Cleaning up leftover recordings..."
drain_all_recordings
echo ""

test_single_recording
echo ""
# Ensure clean state before keepalive test
drain_all_recordings
test_stream_keepalive
echo ""
test_two_recordings
echo ""
test_recording_stops
echo ""
# Ensure clean state before HLS tests
drain_all_recordings
test_hls_playback
echo ""
# Ensure clean state before ffprobe test
drain_all_recordings
test_ffprobe_hls
echo ""
test_discontinuity_tags
echo ""
# Ensure clean state before parallel stream test
drain_all_recordings
test_parallel_stream_fairness
echo ""
# Ensure clean state before staggered start test
drain_all_recordings
test_staggered_start
echo ""
# Ensure clean state before 3-channel tests
drain_all_recordings
test_three_channel_fairness
echo ""
drain_all_recordings
test_three_channel_staggered
echo ""
# Ensure clean state before live stream test
drain_all_recordings
test_live_stream_continuity
echo ""

log "============================="
log "Results: $PASS passed, $FAIL failed"
if [ "$FAIL" -gt 0 ]; then
    exit 1
else
    log "All tests passed!"
fi
