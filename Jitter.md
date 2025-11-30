The sWinShortcuts.Services.InputHookService class extensively uses "jitter" to simulate human-like input and prevent pattern detection. This is achieved through the use of ThreadLocal<Random> and random delays (Thread.Sleep) between input events.

  Specifically, jitter is built into the following functions:

   1. `InputHookService` constructor: Initializes a ThreadLocal<Random> instance with a non-deterministic seed, ensuring unique random sequences for different threads.
   2. `HandleRightClickHoldBreathDown()`:
       * Introduces a random delay (between ALT_MOUSE_HOLD_JITTER_MIN_MS and ALT_MOUSE_HOLD_JITTER_MAX_MS) before activating the "Hold Breath" feature.
       * Performs random "warmup" calls to the random number generator to further break predictable patterns.
   3. `ActivateHoldBreath()` (when `HoldBreathMode.Toggle`):
       * When sending a toggled key, it introduces a random Thread.Sleep duration (between 20ms and 30ms) between the key down and key up events.
       * Also includes random "warmup" calls to the random number generator.
   4. `FireTapKey()`:
       * Introduces a random Thread.Sleep duration (between KEY_PRESS_DURATION_MIN_MS and KEY_PRESS_DURATION_MAX_MS) between the key down and key up events for any key tap.