namespace Partner.Smoke;

internal static partial class Program
{
    /// <summary>
    /// Capability declarations: a capability's level is derived from smoke checks that actually ran —
    /// never from configuration.
    /// </summary>
    private static readonly (string Id, string Owner, string[] Checks)[] Capabilities =
    {
        ("session.append_only_log", "M1", new[]
        {
            "session log opens clean",
            "session has 6 content events (3 user + 3 assistant)",
            "raw log keeps every event (edits included)",
            "restart: session log still clean",
            "restart: last seq preserved",
            "restart: seq continues without reuse",
        }),
        ("session.editable_projection", "M1", new[]
        {
            "edit replaces the projected body",
            "edit keeps the original body",
            "edit is not rendered as its own content",
            "revision count is 1",
            "projection is rebuildable from the log",
        }),
        ("queue.single_fifo", "M2", new[]
        {
            "queue depth counts the three distinct items",
            "running item is visible while executing",
            "consumed in arrival order",
            "queue drained",
            "no running item after drain",
        }),
        ("queue.idempotent_restart", "M2", new[]
        {
            "duplicate idempotency key reuses the seq",
            "restart: consumed idempotency key still dedupes",
            "restart: new item gets a fresh seq",
        }),
        ("queue.crash_recovery", "M2", new[]
        {
            "crash recovery: interrupted item is re-queued",
            "crash recovery: attempt counter advanced",
        }),
        ("agent.turn_state_machine", "M3", new[]
        {
            "turn: item is claimed before the turn",
            "turn: completes with reason=completed",
            "turn: two steps (tool then answer)",
            "turn: event order matches the spec (assistant only when the step has text)",
            "turn: projection shows the answer but not control events",
        }),
        ("agent.tool_loop", "M3", new[]
        {
            "turn: one tool call executed",
        }),
        ("agent.hook_phases", "M3", new[]
        {
            "turn: hook injection is recorded as a system event",
        }),
        ("skills.workspace_scan", "M8", new[]
        {
            "skills: workspace scan finds valid skills and warns about bad ones",
        }),
        ("skills.injection", "M8", new[]
        {
            "skills: injection reaches the model prompt",
            "skills: injection is recorded in the session",
        }),
        ("scheduler.durable_delivery", "M4", new[]
        {
            "scheduler: task delivers one queue item",
            "scheduler: item carries the (taskId, occurrenceAt) key",
            "scheduler: task advances to the next occurrence",
        }),
        ("scheduler.receipt_dedup", "M4", new[]
        {
            "scheduler: restart does not re-deliver the same occurrence",
        }),
        ("scheduler.failure_retention", "M4", new[]
        {
            "scheduler: failed delivery keeps the obligation",
            "scheduler: a failing task is not lost and recovers",
        }),
        ("scheduler.schedule_to_turn", "M4", new[]
        {
            "scheduler: scheduled item is claimable",
            "scheduler: scheduled item runs as a turn",
            "scheduler: session records the schedule turn",
        }),
        ("memory.recall_pipeline", "M5", new[]
        {
            "memory: recall finds the relevant record and not the noise",
            "memory: recall is explainable",
            "memory: a hit carries the tier and retention of the record it matched",
        }),
        ("memory.offline_degradation", "M5", new[]
        {
            "memory: offline (no embeddings) still recalls",
        }),
        ("memory.privacy_delete", "M5", new[]
        {
            "memory: privacy delete purges, hides from search, then compacts",
        }),
        ("memory.promotion", "M5", new[]
        {
            "memory: promotion preserves content as an episodic copy",
        }),
        ("memory.injection_budget", "M5", new[]
        {
            "memory: injection respects the token budget",
            "memory: the recall hook injects through the budget and marks the cut",
        }),
        ("memory.maintenance_pass", "M0", new[]
        {
            "memory: the maintenance pass ages and trims an untouched store",
        }),
        ("memory.embeddings_switch", "M0", new[]
        {
            "memory: embeddings default off and the switch round-trips",
        }),
        ("settings.no_model_shrink_saves", "M0", new[]
        {
            "settings: shrinking the window without a model saves and reports the deferred compaction",
        }),
        ("memory.scale_p99", "M5", new[]
        {
            "memory: 100000 records — retrieval p90 under 1000ms",
        }),
        ("memory.scale_p99_cjk", "M5", new[]
        {
            "memory: 100000 Chinese records — retrieval p90 under 1000ms",
        }),
        ("governance.policy_decisions", "M6", new[]
        {
            "governance: policy allows a listed tool",
            "governance: explicit deny is refused",
        }),
        ("governance.fail_closed", "M6", new[]
        {
            "governance: unlisted tool is refused (fail-closed)",
        }),
        ("governance.target_param_rules", "M6", new[]
        {
            "governance: target parameter rules reject an out-of-scope target",
        }),
        ("governance.enforced_in_turn", "M6", new[]
        {
            "governance: denial is enforced inside a real turn",
        }),
        ("mcp.stdio_lifecycle", "M7", new[]
        {
            "mcp: test-double server is built",
            "mcp: server starts and lists tools",
        }),
        ("mcp.tool_bridge", "M7", new[]
        {
            "mcp: tool call round-trips through the client",
        }),
        ("mcp.governed_turn", "M7", new[]
        {
            "mcp: mcp tool runs inside a governed turn",
        }),
        ("desktop.window_enumeration", "M9", new[]
        {
            "desktop: window enumeration finds visible windows",
        }),
        ("desktop.gdi_capture", "M9", new[]
        {
            "desktop: GDI capture returns pixels",
        }),
        ("desktop.post_action_verification", "M9", new[]
        {
            "desktop: post-action verification measures rather than assuming",
        }),
        ("desktop.lease_gating", "M9", new[]
        {
            "desktop: lease is issued per entity with a unique generation",
            "desktop: action on an unknown entity is refused without injecting",
            "desktop: an expired lease refuses the action without injecting",
        }),
        ("desktop.input_marshalling", "M9", new[]
        {
            "desktop: input marshalling matches the Win32 contract",
            "desktop: positional input is absolute in the 0..65535 virtual-desktop space",
            "desktop: a point outside the virtual desktop is refused instead of clamped",
        }),
        // Only the routing half is registered here, because it is the half the smoke can observe without
        // injecting anything. That a named button really is pressed through its pattern is proven by
        // DesktopLiveInteractionTests, which is opt-in: it moves the pointer and shows a window.
        ("desktop.uia_pattern_routing", "M9", new[]
        {
            "desktop: an action a pattern cannot express is routed to injected input",
            "desktop: a control id that does not resolve is not guessed at",
        }),
        // The draw itself (grid lines, chips, the scaled-picture offset) is covered by ScreenshotMarksTests and
        // by the opt-in live test that compares a mark against the application's own PointToScreen; what is
        // registered here is the part the model consumes, which the smoke can observe read-only.
        ("desktop.screenshot_marks", "M9", new[]
        {
            "desktop: a picture's marks carry each control's real screen point",
        }),
        ("desktop.minimized_window", "M9", new[]
        {
            "desktop: a minimized window is reported as minimized rather than as a small window",
            "desktop: a minimized window is refused as a picture instead of a 160×28 stub",
            "desktop: a click into a minimized window's placeholder rect is refused",
            "desktop: focusing a minimized window restores it instead of only lighting up the taskbar",
            "desktop: a restored window is screenshot from the stale observation instead of being refused",
        }),
        ("desktop.act_targeting", "M9", new[]
        {
            "desktop: a point with no window id resolves to the window that covers it",
            "desktop: a point no window covers is refused with the candidates named",
            "desktop: the window under a point is the top-level window that contains it",
        }),
        ("desktop.model_vision", "M9", new[]
        {
            "desktop: a screenshot reaches the model as an image",
            "desktop: the log records the screenshot by media id, not by pixels",
        }),
        // The act answer carries the state after the action. The decision and the recording are what the
        // smoke can drive read-only (the positive click itself is a live test's job, which moves the user's
        // pointer and is deliberately skipped here).
        ("desktop.act_returns_state", "M9", new[]
        {
            "desktop: an act answer promises a picture for the kinds that change what is seen",
            "desktop: an act answer's attached picture maps back and is recorded by id",
        }),
        ("desktop.step_budget_by_family", "M9", new[]
        {
            "desktop: the step budget is measured by tool family, not one number",
        }),
        ("desktop.task_lease_approval", "M9", new[]
        {
            "desktop: an approved action opens a continuing grant on its window",
            "desktop: a granted window does not re-ask for the same kind",
            "desktop: a grant never covers leaving the GUI even while one is live",
        }),
        ("desktop.wait_primitive", "M9", new[]
        {
            "desktop: a wait for a control that is already there answers at once with the new id",
        }),
        ("desktop.desktop_scope", "M9", new[]
        {
            "desktop: a full-desktop picture exists without any observation and is recorded as desktop",
            "desktop: px/py on the desktop picture resolves to the point it was taken from",
        }),
        ("desktop.px_snap_to_element", "M9", new[]
        {
            "desktop: a px/py miss within the snap radius resolves to the control it was for",
        }),
        ("desktop.telemetry_aggregates", "M9", new[]
        {
            "desktop: telemetry aggregates the turn the model just ran",
        }),
        ("voice.utterance_to_queue", "M10", new[]
        {
            "voice: speech followed by silence produces one utterance",
            "voice: the utterance becomes a queue item with a boot-scoped key",
        }),
        ("voice.asr_contract", "M10", new[]
        {
            "voice: the ASR request carried a 16 kHz mono WAV",
        }),
        ("voice.playback_gate", "M10/M11", new[]
        {
            "voice: playback closed the gate",
            "voice: input during playback is discarded and reported",
            "voice: reopening the gate reports the discarded audio",
            "voice: gate reports its blockers while closed",
        }),
        ("voice.output_ordering", "M11", new[]
        {
            "voice: output speaks sentences in order, cleaned of markdown",
        }),
        ("voice.threshold_guard", "M10", new[]
        {
            "voice: a threshold at or below the noise floor is detected",
        }),
        ("voice.input_level", "M10", new[]
        {
            "voice: the reported input level rises while the microphone hears speech",
            "voice: the reported input level falls back when the speaking stops",
            "voice: speech at startup is not mistaken for the noise floor",
        }),
        ("voice.failure_visibility", "M10/M11", new[]
        {
            "voice: an unreachable ASR service is reported and produces no utterance",
            "voice: a dead TTS service fails visibly without stalling playback",
        }),
        ("voice.output_switch", "M11", new[]
        {
            "voice: output disabled means no synthesis and no playback",
        }),
        ("voice.hosted_loop", "M10/M11", new[]
        {
            "voice: enabling the halves starts them on the configured devices",
            "voice: an utterance is answered by a turn and the answer is spoken",
            "voice: the utterance is consumed and the halves stay up",
            "voice: a device that will not open is reported without stopping the other half",
        }),
        ("game.process_adapter", "M12", new[]
        {
            "game: adapter attaches to a real game process and reads its state",
            "game: the action outcome carries the post-action state",
            "game: detaching stops the child process",
        }),
        ("game.reflex_loop", "M12", new[]
        {
            "game: a reflex behavior acts and the world changes",
            "game: an async reflex behavior is not preempted mid-run",
        }),
        ("game.sensor_debounce", "M12", new[]
        {
            "game: sensor chatter debounces while action signals never do",
        }),
        ("game.latency_monitor", "M12", new[]
        {
            "game: latency is measured, not assumed",
        }),
        ("ui.auth", "M13", new[]
        {
            "ui: an unauthenticated request is rejected on every endpoint",
            "ui: a wrong token is rejected and the right one is accepted",
        }),
        ("agent.queue_consumer", "M3", new[]
        {
            "consumer: without a model it is disabled, says why, and leaves work queued",
            "consumer: with a model the delivered input becomes a turn in the session",
            "consumer: the queue drains and the loop counts what it handled",
            "consumer: a real HTTP model call completes a turn end to end",
            "session: the reply is visible while it is still being written",
            "session: the client asks the model to stream",
            "session: the finished reply replaces the live row, whole",
            "session: tool calls and results reach the view",
            "image: an upload comes back as an id",
            "image: the model receives the attachment inline",
            "image: the logs keep the id, not the bytes",
            "desktop: a key press waits for approval",
            "desktop: an approved action looks again instead of trusting a dead lease",
            "desktop: a screenshot reaches the model as an image",
            "desktop: the log records the screenshot by media id, not by pixels",
            "desktop: the default gear asks for every action but never for looking",
            "desktop: the middle gear frees the GUI and still gates commands",
            "desktop: the open gear asks for nothing",
            "desktop: the capability reports itself enabled",
            "desktop: the model can observe through its tool",
            "desktop: an action waits for approval instead of running",
            "desktop: the approval queue surfaces the parked action",
            "desktop: answering the queue settles it",
            "desktop: a denied action is refused, not performed",
            "desktop: the refusal is in the log with the tool's name",
            "settings: pointing the model settings at an endpoint re-points the consumer",
            "settings: the API key is sent to the model but never echoed back",
            "settings: the thinking gear reaches the model request",
            "input: a gear chosen for one message wins over the configured default",
            "input: a gear that is not on the menu is rejected and lists the ones that are",
            "settings: the model listing is pulled through the API",
            "settings: an unreachable model endpoint returns a reason, not a silent empty list",
            "settings: shrinking the context compacts the history once",
            "settings: the compaction leaves exactly one summary standing in for the history",
        }),
        ("session.removal", "M1", new[]
        {
            "session: deleting the latest conversation takes its text out of the log",
            "session: withdrawing hides the conversation but leaves its text in the log",
            "session: a withdrawal can be cancelled before anything new is sent",
            "session: sending a new message makes the withdrawal final",
            "session: events after a retraction stay visible",
        }),
        ("ui.composer", "M13", new[]
        {
            "ui: submitting from the composer queues work without executing it",
        }),
        ("ui.session_view", "M13", new[]
        {
            "ui: session view shows the edit, keeps the original, and re-runs nothing",
        }),
        ("ui.memory_view", "M13", new[]
        {
            "ui: memory edit records the actor and the revision",
            "ui: memory delete removes it from search and from the list",
        }),
        ("ui.queue_view", "M13", new[]
        {
            "ui: queue view shows state and honours cancel",
        }),
        ("ui.settings", "M13", new[]
        {
            "ui: settings round-trip every section through the API",
        }),
        ("ui.projection_rebuild", "M13", new[]
        {
            "ui: rebuilding projections reproduces the same session view",
        }),
    };
}
