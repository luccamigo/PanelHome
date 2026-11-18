# Android session lifecycle

## Ownership

`PanelSessionService` is the only production owner of the selected
`PanelTransport`, `TransportPanelConnection`, `TransportVideoConnection`, and
`PanelSessionCoordinator`. It is a started foreground service and also exposes a
process-local binder. Compose is not referenced by the service.

`MainActivity` starts the service and binds while visible. `PanelViewModel` only
projects the service `StateFlow` and forwards UI intents. Unbinding or clearing a
ViewModel does not close the session. A recreated Activity binds to the same
service runtime, immediately receives its latest snapshot, and does not create a
second connection.

## USB and reconnect

The service owns accessory attach, detach, and permission handling, and it does
so through one explicit state machine rather than through several booleans. The
full lifecycle, the timeouts, the descriptor ownership rules and the measured
device limitations are in [`native-usb-recovery.md`](native-usb-recovery.md);
what matters here is the split it enforces.

A **physical detach** is not a session failure. It closes the session and the
descriptor and leaves the panel in `WAITING_FOR_ACCESSORY`: no descriptor, no
transport, and no timer. Nothing calls `openAccessory` again until a real
`ACTION_USB_ACCESSORY_ATTACHED` arrives, because after a cable pull there is
nothing to open.

A **host restart** with the cable still in place does not touch the descriptor.
PLMX v2 adopts the newest HELLO on the connection it already has, the control
connection's own retry loop repeats the Panel Protocol handshake, and the
lifecycle only rearms its application-handshake deadline.

A **transport failure with the cable still present** recycles the session:
close-before-open on a fresh descriptor, with a bounded backoff, and never in
parallel with another attempt.

No path terminates the process. PanelHome is the HOME app, so a recovery that
needs a new process is the tablet's home screen disappearing; `NoProcessExitTest`
fails the build if `exit`, `killProcess` or `finishAffinity` reappear in the
Android runtime.

Each concrete coordinator has its own child coroutine scope. Closing it cancels
all old collectors and queues before its transport is closed. The coordinator's
disconnect path cancels the active touch gesture, clears video frame/decoder
state, drops the old Stream Deck layout, and waits for the host to republish
session state after the next application handshake. Partial control or video
streams are never reused across a replacement.

## Android lifecycle semantics

- Activity destruction, configuration changes, HOME foreground/background
  transitions, and ordinary unbinding leave the started service and session
  alive.
- Screen off/on does not itself stop the service; actual device/vendor power
  behavior still requires physical validation.
- A foreground service is not a process-death guarantee. This service returns
  `START_STICKY`, so Android may recreate it according to platform policy and
  current background-start restrictions. On recreation it selects native USB
  when any accessory is attached - permission is requested rather than assumed,
  because an unapproved accessory that forced the ADB path would keep the panel
  there even after the operator approved it. Otherwise it starts the ADB
  development path without persisting the authentication token.
- No boot receiver is introduced. Restart after force-stop, reboot, or a device
  policy kill remains governed by Android and the HOME app launch lifecycle.
- Neither an Activity nor a ViewModel participates in USB recovery. They may
  forward an accessory intent to the service; the decision and the descriptor
  ownership stay in the service.
