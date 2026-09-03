package com.smatechnology.denimrolls.ui;

import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.button.MaterialButton;
import com.smatechnology.denimrolls.R;
import com.smatechnology.denimrolls.data.ApiClient;
import com.smatechnology.denimrolls.data.AppSettings;
import com.smatechnology.denimrolls.data.DocumentDetail;
import com.smatechnology.denimrolls.data.DocumentItem;
import com.smatechnology.denimrolls.data.SessionResult;
import com.smatechnology.denimrolls.gate.GateCycle;
import com.smatechnology.denimrolls.rfid.ReaderController;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.TimeZone;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * The gate sheet: one document, the reader, and a verdict.
 *
 * <p>Laid out as the printed form the warehouse already uses, so an operator
 * who knows the paper knows this. Reads are checked against the document as
 * they arrive, and a roll that does not belong is called out immediately --
 * while the load is still in front of them and something can be done about it.
 *
 * <p>That check is a courtesy, not the decision. On stop the whole session
 * goes to the server, which re-validates against the database and is the only
 * thing that can move stock.
 *
 * <h3>Two ways to run a gate</h3>
 *
 * By default the operator presses START, the reader reads until STOP, and
 * rolls are released one per interval so the list fills at a pace a person
 * can follow.
 *
 * <p>Where the gate has its own sensor, START instead only opens the session:
 * each time 12V appears on the input the reader reads one roll and stops
 * again, and the roll that produced no tag is called out the moment the
 * signal drops rather than at the end of the load. The buttons still bound
 * the session, so the gate can do nothing at all unless a document is open --
 * and where the sensor is fed from a reader output, START and STOP switch
 * that supply, so it is true electrically as well.
 */
public final class ScanActivity extends AppCompatActivity implements ReaderController.Listener {

    public static final String EXTRA_DOCUMENT_ID = "document_id";

    /** How long an alarm stays on screen before clearing itself. */
    private static final long ALARM_VISIBLE_MS = 5000L;

    private final ExecutorService io = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());

    /** Distinct tags accepted this session, in arrival order. */
    private final Set<String> accepted = new LinkedHashSet<>();

    /**
     * Tags read that are not on this document, in arrival order.
     *
     * <p>Held apart from the document's own rows so that rebuilding the list
     * cannot lose them. An unknown roll has to be set aside by hand, and the
     * operator only knows which one it was because it is named on this list;
     * having it disappear when the next roll is read would be the worst kind
     * of quiet failure.
     */
    private final Set<String> strays = new LinkedHashSet<>();

    private final List<Row> rows = new ArrayList<>();

    private final Runnable clearAlarm = this::restoreStatus;
    private Runnable silenceCheck;

    private ApiClient api;
    private AppSettings settings;
    private ReaderController reader;
    private DocumentDetail document;

    private int documentId;

    /**
     * When the gate reads, when it stops, and when it refuses to carry on.
     *
     * <p>Kept out here with no Android in it so the rules can be tested at a
     * desk. This screen does what it is told and owns none of the decisions.
     */
    private final GateCycle gate = new GateCycle();

    private long sessionStartedAt;
    private long lastReadAt;
    private String sessionKey;

    private TextView documentNumber;
    private TextView movement;
    private TextView userName;
    private TextView totalArticles;
    private TextView totalQuantity;
    private TextView balanceArticles;
    private TextView balanceQuantity;
    private TextView statusText;
    private TextView lastEpc;
    private View statusPanel;

    /** What the status band should say once an alarm has cleared. */
    private String restingStatus = "";
    private int restingText = R.color.info;
    private int restingBackground = R.color.info_soft;
    private MaterialButton startButton;
    private MaterialButton stopButton;
    private MaterialButton resetButton;
    private RowAdapter adapter;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        setTheme(R.style.Theme_DenimRolls_Scan);
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_scan);

        api = new ApiClient(this);
        settings = new AppSettings(this);
        documentId = getIntent().getIntExtra(EXTRA_DOCUMENT_ID, 0);

        documentNumber = findViewById(R.id.document_number);
        movement = findViewById(R.id.movement);
        userName = findViewById(R.id.user_name);
        totalArticles = findViewById(R.id.total_articles);
        totalQuantity = findViewById(R.id.total_quantity);
        balanceArticles = findViewById(R.id.balance_articles);
        balanceQuantity = findViewById(R.id.balance_quantity);
        statusText = findViewById(R.id.status_text);
        lastEpc = findViewById(R.id.last_epc);
        statusPanel = findViewById(R.id.status_panel);
        startButton = findViewById(R.id.start);
        stopButton = findViewById(R.id.stop);
        resetButton = findViewById(R.id.reset);

        RecyclerView list = findViewById(R.id.rolls);
        list.setLayoutManager(new LinearLayoutManager(this));
        adapter = new RowAdapter();
        list.setAdapter(adapter);

        userName.setText(api.displayName());

        reader = new ReaderController(this);
        reader.setListener(this);

        startButton.setOnClickListener(v -> startSession());
        stopButton.setOnClickListener(v -> confirmStop());
        resetButton.setOnClickListener(v -> resetAlarm());
        findViewById(R.id.back).setOnClickListener(v -> finish());

        loadDocument();
    }

    // -------------------------------------------------------------- document

    private void loadDocument() {
        setStatus(getString(R.string.scan_loading), R.color.info, R.color.info_soft);

        io.execute(() -> {
            try {
                DocumentDetail loaded = api.getDocument(documentId);

                runOnUiThread(() -> {
                    document = loaded;
                    render();

                    // Open the module here so the delay is not on the critical
                    // path when somebody is standing at the gate.
                    io.execute(() -> {
                        if (reader.initialise()) {
                            runOnUiThread(() -> reader.startGateInputWatch());
                        }
                    });
                });
            } catch (ApiClient.ApiException e) {
                runOnUiThread(() -> {
                    setStatus(getString(R.string.scan_load_failed), R.color.alarm, R.color.alarm_soft);
                    showAlarm(getString(R.string.scan_load_failed), e.getMessage());
                });
            }
        });
    }

    private void render() {
        documentNumber.setText(document.documentNumber);
        movement.setText(document.type.toUpperCase(Locale.US));
        totalArticles.setText(String.valueOf(document.expectedArticles));
        totalQuantity.setText(String.valueOf(document.expectedQuantity));

        rebuildRows();
        updateFigures();

        setStatus(getString(R.string.scan_ready), R.color.info, R.color.info_soft);
        startButton.setEnabled(true);
    }

    private void rebuildRows() {
        rows.clear();

        // Strays first: the row that needs acting on is the row worth seeing
        // without scrolling.
        for (String epc : strays) {
            rows.add(Row.stray(getString(R.string.scan_unknown_roll), epc));
        }

        for (DocumentItem item : document.items) {
            rows.add(Row.of(item));
        }

        adapter.notifyDataSetChanged();
    }

    private void updateFigures() {
        balanceArticles.setText(String.valueOf(document.outstanding()));
        balanceQuantity.setText(String.valueOf(document.outstandingQuantity()));
    }

    // --------------------------------------------------------------- session

    private void startSession() {
        if (document == null) {
            return;
        }

        accepted.clear();
        strays.clear();
        lastEpc.setText("");
        sessionKey = UUID.randomUUID().toString();
        sessionStartedAt = System.currentTimeMillis();

        for (DocumentItem item : document.items) {
            item.scannedNow = false;
        }

        rebuildRows();
        updateFigures();
        hideAlarm();
        reader.cancelAlarm();

        if (gate.start(settings.gpioStartEnabled()) == GateCycle.Action.START_READING) {
            if (!reader.start()) {
                // Put the session back. An open session that is not reading
                // would show STOP over a reader that never started.
                gate.stop();

                return;
            }

            showButtons();
            setStatus(getString(R.string.scan_reading), R.color.ok, R.color.ok_soft);
            armSilenceWatch();

            return;
        }

        // Gate-driven: nothing is read yet. The beam says when, one roll at a
        // time, and the session is only the window in which it may.
        showButtons();
        setStatus(getString(R.string.scan_gate_armed), R.color.info, R.color.info_soft);
    }

    private void showButtons() {
        startButton.setVisibility(gate.isSessionOpen() ? View.GONE : View.VISIBLE);
        stopButton.setVisibility(gate.isSessionOpen() ? View.VISIBLE : View.GONE);
        resetButton.setVisibility(gate.isLatched() ? View.VISIBLE : View.GONE);
    }

    // ----------------------------------------------------------- the latch

    /**
     * Holds the gate until somebody says the roll has been dealt with.
     *
     * <p>An alarm here does not time out. A wrong roll and a roll that went
     * through untagged both leave something physical to sort out, and a timer
     * cannot sort it out; clearing the alarm by itself would only mean the
     * warning had been outlasted. So the reader stops, the alarm keeps
     * sounding, and the one thing on offer is RESET.
     */
    private void latchAlarm(String title, String body) {
        disarmSilenceWatch();
        reader.stop();

        ui.removeCallbacks(clearAlarm);
        paintAlarm(title, body);
        showButtons();
    }

    /** Somebody has dealt with it. Back to reading. */
    private void resetAlarm() {
        GateCycle.Action next = gate.reset();

        reader.cancelAlarm();
        hideAlarm();
        showButtons();

        if (next == GateCycle.Action.RESUME_WAITING) {
            setStatus(getString(R.string.scan_reset_done), R.color.info, R.color.info_soft);

            return;
        }

        if (next == GateCycle.Action.RESUME_READING && reader.start()) {
            setStatus(getString(R.string.scan_reading), R.color.ok, R.color.ok_soft);
            armSilenceWatch();
        }
    }

    // ----------------------------------------------------------- gate cycle

    /**
     * One roll: the signal arrives, the reader reads, the signal drops.
     *
     * <p>Unpaced, because the gate is already doing the pacing. Holding tags
     * back on top of a signal that lasts a second or two would only lose
     * them.
     */
    private void beginCycle() {
        reader.start(false);

        // A new roll at the gate means the last one has been dealt with.
        reader.cancelAlarm();
        hideAlarm();

        setStatus(getString(R.string.scan_gate_reading), R.color.ok, R.color.ok_soft);
    }

    private void endCycle() {
        reader.stop();

        // stop() releases whatever was still queued, and those arrive on this
        // thread; the verdict has to wait until they have.
        ui.post(this::judgeCycle);
    }

    /**
     * A roll went past and nothing answered.
     *
     * <p>Reported here rather than at the end of the load, because here the
     * roll is still within arm's reach.
     */
    private void judgeCycle() {
        setStatus(getString(R.string.scan_gate_waiting), R.color.info, R.color.info_soft);

        if (gate.judge() == GateCycle.Action.LATCH_MISSED_ROLL) {
            reader.signalMissedRoll();
            latchAlarm(getString(R.string.scan_no_tag_title),
                    getString(R.string.scan_cycle_no_tag));
        }
    }

    private void confirmStop() {
        new AlertDialog.Builder(this)
                .setMessage(R.string.scan_confirm_stop)
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.stop, (d, w) -> stopSession())
                .show();
    }

    private void stopSession() {
        gate.stop();

        disarmSilenceWatch();
        reader.stop();
        reader.cancelAlarm();

        showButtons();
        startButton.setEnabled(false);

        setStatus(getString(R.string.scan_stopped), R.color.warn, R.color.warn_soft);

        if (accepted.isEmpty()) {
            reader.signalNoTag();
            showAlarm(getString(R.string.scan_no_tag_title), getString(R.string.scan_no_epc));
        }

        submitSession();
    }

    private void submitSession() {
        final List<String> epcs = new ArrayList<>(accepted);
        final int raw = reader.rawReadCount();
        final boolean healthy = reader.isHealthy();
        final String started = iso(sessionStartedAt);
        final String completed = iso(System.currentTimeMillis());
        final String key = sessionKey;

        io.execute(() -> {
            try {
                SessionResult result = api.submitSession(
                        documentId, epcs, raw, healthy, key, started, completed);

                runOnUiThread(() -> showResult(result));
            } catch (ApiClient.ApiException e) {
                runOnUiThread(() -> {
                    startButton.setEnabled(true);
                    showAlarm(getString(R.string.scan_submit_failed), e.getMessage());
                });
            }
        });
    }

    private void showResult(SessionResult result) {
        startButton.setEnabled(true);

        String headline = headline(result);
        StringBuilder detail = new StringBuilder(headline);

        if (result.movedArticles > 0) {
            detail.append("\n\n").append(getString(
                    R.string.scan_committed, result.movedArticles, result.movedQuantity));
        }

        appendList(detail, getString(R.string.scan_missing_label), result.missing);
        appendList(detail, getString(R.string.scan_unknown_label), result.unknown);
        appendList(detail, getString(R.string.scan_unexpected_label), result.unexpected);

        new AlertDialog.Builder(this)
                .setTitle(result.passed ? R.string.scan_pass : R.string.scan_fail)
                .setMessage(detail.toString())
                .setPositiveButton(android.R.string.ok, null)
                .show();

        if (result.passed) {
            reader.signalAccepted();
            hideAlarm();
            setStatus(getString(R.string.scan_pass), R.color.ok, R.color.ok_soft);
        } else {
            // The same low tone a wrong roll makes, so a failed load and a
            // wrong roll are one idea rather than two. Timed, not latched:
            // the dialog beside it already needs dismissing.
            reader.signalFailedLoad();
            setStatus(headline, R.color.alarm, R.color.alarm_soft);
        }

        refreshQuietly();
    }

    /**
     * The verdict in words an operator can act on.
     *
     * <p>The server's own summary counts everything precisely and reads like
     * a log line. What matters at the gate is which pile of rolls has a
     * problem, so that is what this says. The server's wording is kept as a
     * fallback for a failure that fits none of these buckets, rather than
     * showing nothing at all.
     */
    private String headline(SessionResult result) {
        if (result.passed) {
            return getString(R.string.scan_all_good, result.detectedCount);
        }

        StringBuilder parts = new StringBuilder();

        append(parts, R.string.scan_missing_label, result.missing.size());
        append(parts, R.string.scan_unknown_label, result.unknown.size());
        append(parts, R.string.scan_unexpected_label, result.unexpected.size());

        return parts.length() == 0 ? result.summary : parts.toString();
    }

    private void append(StringBuilder parts, int label, int count) {
        if (count == 0) {
            return;
        }

        if (parts.length() > 0) {
            parts.append("  ·  ");
        }

        parts.append(getString(R.string.scan_count_line, getString(label), count));
    }

    private void refreshQuietly() {
        io.execute(() -> {
            try {
                DocumentDetail loaded = api.getDocument(documentId);

                runOnUiThread(() -> {
                    document = loaded;
                    documentNumber.setText(document.documentNumber);
                    totalArticles.setText(String.valueOf(document.expectedArticles));
                    totalQuantity.setText(String.valueOf(document.expectedQuantity));
                    rebuildRows();
                    updateFigures();
                });
            } catch (ApiClient.ApiException ignored) {
                // The verdict is already on screen; a failed refresh is not
                // worth a second error in front of the operator.
            }
        });
    }

    // ---------------------------------------------------------- reader hooks

    @Override
    public void onReaderReady(String version) {
        // Readiness is implied by START being enabled.
    }

    @Override
    public void onReaderError(String message) {
        showAlarm(getString(R.string.scan_reader_title), message);
    }

    @Override
    public void onInventoryStateChanged(boolean running) {
        // The buttons follow the session, not the module: in gate mode the
        // module starts and stops for every roll and the buttons must not
        // flicker along with it.
        showButtons();
    }

    /**
     * The gate's own signal, where the reader is wired to one.
     *
     * <p>Ignored unless a document is open. The gate does not decide what is
     * being loaded; the operator does, by choosing a document and pressing
     * START.
     */
    @Override
    public void onGateSignal(boolean active) {
        // Whether this is a roll at all -- rather than a chattering beam, or
        // one arriving while a latched alarm holds the gate -- is the state
        // machine's call, not this screen's.
        GateCycle.Action next = active ? gate.beamBroken() : gate.beamRestored();

        if (next == GateCycle.Action.START_READING) {
            beginCycle();
        } else if (next == GateCycle.Action.JUDGE_ROLL) {
            endCycle();
        }
    }

    @Override
    public void onEpcAccepted(String epc, String rssi, String antenna) {
        if (document == null) {
            return;
        }

        lastEpc.setText(epc);
        lastReadAt = System.currentTimeMillis();

        // Counted before the duplicate check: the cycle's question is whether
        // anything answered, not whether it was new. A roll sent through
        // twice has a working tag either way.
        gate.tagSeen();

        if (!accepted.add(epc)) {
            return;
        }

        DocumentItem item = document.findByEpc(epc);

        if (item == null) {
            // What matters on the floor is "not on this document". Whether the
            // tag is unknown to the warehouse entirely is the server's call and
            // comes back with the verdict.
            if (gate.wrongRoll() == GateCycle.Action.LATCH_WRONG_ROLL) {
                reader.signalWrongRoll();
                latchAlarm(getString(R.string.scan_invalid_title),
                        getString(R.string.scan_invalid_body, document.documentNumber));
            }

            strays.add(epc);
            rebuildRows();

            return;
        }

        item.scannedNow = true;
        reader.signalAccepted();
        rebuildRows();
        updateFigures();
    }

    // -------------------------------------------------------------- watchdog

    /**
     * Raises the alarm when nothing has been read for the configured window.
     *
     * <p>A roll going past without a working tag looks exactly like silence,
     * so silence is what gets watched. It fires once per quiet spell: an
     * operator who has been told does not need telling four times a second.
     */
    private void armSilenceWatch() {
        final int window = settings.noReadTimeoutMs();

        if (window <= 0) {
            return;
        }

        lastReadAt = System.currentTimeMillis();

        silenceCheck = new Runnable() {
            @Override
            public void run() {
                if (!reader.isRunning()) {
                    return;
                }

                if (System.currentTimeMillis() - lastReadAt >= window) {
                    reader.signalNoTag();

                    // signalNoTag ignores a repeat while the previous alarm is
                    // still sounding, so this keeps the message on screen at
                    // the alarm's own rhythm instead of once and then silence.
                    showAlarm(getString(R.string.scan_no_tag_title),
                            getString(R.string.scan_no_silence));
                }

                ui.postDelayed(this, Math.max(200, window / 2));
            }
        };

        ui.postDelayed(silenceCheck, window);
    }

    private void disarmSilenceWatch() {
        if (silenceCheck != null) {
            ui.removeCallbacks(silenceCheck);
            silenceCheck = null;
        }
    }

    // ---------------------------------------------------------------- chrome

    /**
     * Sets the resting state of the status band and shows it.
     *
     * <p>Remembered separately from an alarm, so when the alarm clears the
     * band goes back to describing what the reader is actually doing rather
     * than to whatever it happened to say last.
     */
    private void setStatus(String text, int textColour, int backgroundColour) {
        restingStatus = text;
        restingText = textColour;
        restingBackground = backgroundColour;

        ui.removeCallbacks(clearAlarm);
        restoreStatus();
    }

    private void restoreStatus() {
        statusText.setText(restingStatus);
        statusText.setTextColor(getColor(restingText));
        statusPanel.setBackgroundColor(getColor(restingBackground));
        lastEpc.setTextColor(getColor(R.color.ink));
    }

    /**
     * Puts a problem in the status band, in place of the running commentary.
     *
     * <p>It shares the band rather than floating over the sheet: a separate
     * card covered the balance figures, and those are the one thing that must
     * stay visible. Clears itself after {@link #ALARM_VISIBLE_MS}, and each
     * new alarm restarts the timer.
     */
    private void showAlarm(String title, String body) {
        paintAlarm(title, body);

        ui.removeCallbacks(clearAlarm);
        ui.postDelayed(clearAlarm, ALARM_VISIBLE_MS);
    }

    /** Puts the problem in the band and leaves it there. */
    private void paintAlarm(String title, String body) {
        statusText.setText(getString(R.string.status_alarm_line, title, body));
        statusText.setTextColor(getColor(R.color.page));
        statusPanel.setBackgroundColor(getColor(R.color.alarm));
        lastEpc.setTextColor(getColor(R.color.page));
    }

    private void hideAlarm() {
        ui.removeCallbacks(clearAlarm);
        restoreStatus();
    }

    private static void appendList(StringBuilder sb, String label, List<String> values) {
        if (values.isEmpty()) {
            return;
        }

        sb.append("\n\n").append(label).append(" (").append(values.size()).append(")");

        for (int i = 0; i < Math.min(6, values.size()); i++) {
            sb.append("\n").append(values.get(i));
        }

        if (values.size() > 6) {
            sb.append("\n+").append(values.size() - 6).append(" more");
        }
    }

    private static String iso(long millis) {
        SimpleDateFormat format = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US);
        format.setTimeZone(TimeZone.getTimeZone("UTC"));

        return format.format(new Date(millis));
    }

    @Override
    protected void onDestroy() {
        ui.removeCallbacksAndMessages(null);
        disarmSilenceWatch();
        reader.release();
        io.shutdownNow();
        super.onDestroy();
    }

    // ------------------------------------------------------------------ rows

    private static final class Row {

        String label;
        String epc;
        boolean scanned;
        boolean committed;
        boolean stray;

        static Row of(DocumentItem item) {
            Row r = new Row();
            r.label = item.label();
            r.epc = item.epc;
            r.scanned = item.scannedNow;
            r.committed = item.isDetected;

            return r;
        }

        static Row stray(String label, String epc) {
            Row r = new Row();
            r.label = label;
            r.epc = epc;
            r.stray = true;

            return r;
        }
    }

    private final class RowAdapter extends RecyclerView.Adapter<RowAdapter.Holder> {

        @NonNull
        @Override
        public Holder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            return new Holder(LayoutInflater.from(parent.getContext())
                    .inflate(R.layout.item_roll, parent, false));
        }

        @Override
        public void onBindViewHolder(@NonNull Holder holder, int position) {
            holder.bind(rows.get(position));
        }

        @Override
        public int getItemCount() {
            return rows.size();
        }

        final class Holder extends RecyclerView.ViewHolder {

            private final TextView code;
            private final TextView tag;
            private final TextView state;
            private final View row;

            Holder(@NonNull View view) {
                super(view);
                code = view.findViewById(R.id.code);
                tag = view.findViewById(R.id.tag);
                state = view.findViewById(R.id.state);
                row = view.findViewById(R.id.row);
            }

            void bind(Row item) {
                code.setText(item.label);
                tag.setText(item.epc);

                // State is carried by a word as well as a colour, never colour
                // alone: a third of older men see red and green differently.
                if (item.stray) {
                    state.setText(R.string.state_invalid);
                    state.setBackgroundColor(getColor(R.color.alarm));
                    row.setBackgroundResource(R.drawable.bg_row_bad);
                } else if (item.scanned) {
                    state.setText(R.string.state_read);
                    state.setBackgroundColor(getColor(R.color.ok));
                    row.setBackgroundResource(R.drawable.bg_row_read);
                } else if (item.committed) {
                    state.setText(R.string.state_done);
                    state.setBackgroundColor(getColor(R.color.ink_muted));
                    row.setBackgroundResource(R.drawable.bg_row);
                } else {
                    state.setText(R.string.state_waiting);
                    state.setBackgroundColor(getColor(R.color.brand));
                    row.setBackgroundResource(R.drawable.bg_row);
                }
            }
        }
    }
}
