## DeskShare 1.6.3

### Better picture
- **Sharp text without a hardware encoder.** VP8 (used when the host has no AMD/NVIDIA/Intel H.264 encoder: Linux, macOS, Windows without a GPU) now runs with settings made for a live screen. 12 px text at 1600x900 went from blurry to sharp.
- **One pointer.** On Linux (X11) and macOS the host's pointer is no longer drawn into the video while you control the computer, so it no longer trails behind your own pointer. People who only watch still see it.
- **Lower delay in the web viewer.** The picture stays close to real time instead of drifting half a second or more behind after a burst. Measured on a Linux host: 110-210 ms instead of 0.5-1 s.

Compatible with 1.6.1 (no protocol change).
