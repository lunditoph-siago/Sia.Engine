// Native calls execute on the browser UI, using the current Wasm heap view.
// The page owns the DOM handlers; no worker or retained typed-array view is used.
addToLibrary({
  sia_browser_read_frame: state => {
    try { Module['siaFrame'].read(HEAPF64, state >>> 3); return 1; }
    catch (error) { console.error('Browser frame input failed:', error); return 0; }
  },
  sia_browser_publish_frame: (distance, flags) => {
    try { Module['siaFrame'].publish(distance, flags); return 1; }
    catch (error) { console.error('Browser frame status failed:', error); return 0; }
  },
  sia_browser_compare_lod__deps: ['$UTF8ToString'],
  sia_browser_compare_lod: pose => {
    try { Module['siaFrame'].compare(UTF8ToString(pose)); return 1; }
    catch (error) { console.error('Browser LOD navigation failed:', error); return 0; }
  }
});
