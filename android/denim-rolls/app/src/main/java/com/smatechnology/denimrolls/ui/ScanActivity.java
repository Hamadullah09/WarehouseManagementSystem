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
 * <p>Scanning starts from the buttons, or from the gate's own 12V signal on a
 * GPIO input where the reader is wired to one.
 */
public final class ScanActivity extends AppCompatActivity implements ReaderController.Listener {

    public static final String EXTRA_DOCUMENT_ID = "document_id";

    /** How long an alarm stays on screen before clearing itself. */
    private static final long ALARM_VISIBLE_MS = 5000L;

    private final ExecutorService io = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());

    /** Distinct tags accepted this session, in arrival order. */
    private final Set<String> accepted = new LinkedHashSet<>();

    private final List<Row> rows = new ArrayList<>();

    private final Runnable clearAlarm = () -> this.alarmPanel.setVisibility(View.GONE);
    private Runnable silenceCheck;

    private ApiClient api;
    private AppSettings settings;
    private ReaderController reader;
    private DocumentDetail document;

    private int documentId;
    private long sessionStartedAt;
    private long lastReadAt;
    private boolean silenceReported;
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
    private View alarmPanel;
    private TextView alarmTitle;
    private TextView alarmBody;
    private MaterialButton startButton;
    private MaterialButton stopButton;
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
        alarmPanel = findViewById(R.id.alarm_panel);
        alarmTitle = findViewById(R.id.alarm_title);
        alarmBody = findViewById(R.id.alarm_body);
        startButton = findViewById(R.id.start);
        stopButton = findViewById(R.id.stop);

        RecyclerView list = findViewById(R.id.rolls);
        list.setLayoutManager(new LinearLayoutManager(this));
        adapter = new RowAdapter();
        list.setAdapter(adapter);

        userName.setText(api.displayName());

        reader = new ReaderController(this);
        reader.setListener(this);

        startButton.setOnClickListener(v -> startSession());
        stopButton.setOnClickListener(v -> confirmStop());
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
                runOnUiThread(() -> showAlarm(getString(R.string.scan_load_failed), e.getMessage()));
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
        sessionKey = UUID.randomUUID().toString();
        sessionStartedAt = System.currentTimeMillis();

        for (DocumentItem item : document.items) {
            item.scannedNow = false;
        }

        rebuildRows();
        updateFigures();
        hideAlarm();

        if (!reader.start()) {
            return;
        }

        startButton.setVisibility(View.GONE);
        stopButton.setVisibility(View.VISIBLE);

        setStatus(getString(R.string.scan_reading), R.color.ok, R.color.ok_soft);
        armSilenceWatch();
    }

    private void confirmStop() {
        new AlertDialog.Builder(this)
                .setMessage(R.string.scan_confirm_stop)
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.stop, (d, w) -> stopSession())
                .show();
    }

    private void stopSession() {
        disarmSilenceWatch();
        reader.stop();

        startButton.setVisibility(View.VISIBLE);
        stopButton.setVisibility(View.GONE);
        startButton.setEnabled(false);

        setStatus(getString(R.string.scan_stopped), R.color.warn, R.color.warn_soft);

        if (accepted.isEmpty()) {
            reader.signalAlarm();
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

        StringBuilder detail = new StringBuilder(result.summary);

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
            reader.signalAlarm();
            setStatus(result.summary, R.color.alarm, R.color.alarm_soft);
        }

        refreshQuietly();
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
        stopButton.setVisibility(running ? View.VISIBLE : View.GONE);
        startButton.setVisibility(running ? View.GONE : View.VISIBLE);
    }

    /** The gate's own signal, where the reader is wired to one. */
    @Override
    public void onGateSignal(boolean active) {
        if (active && !reader.isRunning()) {
            startSession();
        } else if (!active && reader.isRunning()) {
            stopSession();
        }
    }

    @Override
    public void onEpcAccepted(String epc, String rssi, String antenna) {
        if (document == null) {
            return;
        }

        lastEpc.setText(getString(R.string.scan_last_tag, epc));
        lastReadAt = System.currentTimeMillis();
        silenceReported = false;

        if (!accepted.add(epc)) {
            return;
        }

        DocumentItem item = document.findByEpc(epc);

        if (item == null) {
            // What matters on the floor is "not on this document". Whether the
            // tag is unknown to the warehouse entirely is the server's call and
            // comes back with the verdict.
            reader.signalAlarm();
            showAlarm(getString(R.string.scan_invalid_title),
                    getString(R.string.scan_invalid_body, document.documentNumber, epc));

            rows.add(0, Row.stray(epc));
            adapter.notifyItemInserted(0);

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
        final int timeout = settings.noReadTimeoutMs();

        if (timeout <= 0) {
            return;
        }

        lastReadAt = System.currentTimeMillis();
        silenceReported = false;

        silenceCheck = new Runnable() {
            @Override
            public void run() {
                if (!reader.isRunning()) {
                    return;
                }

                if (System.currentTimeMillis() - lastReadAt >= timeout && !silenceReported) {
                    silenceReported = true;
                    reader.signalAlarm();
                    showAlarm(getString(R.string.scan_no_tag_title), getString(R.string.scan_no_epc));
                }

                ui.postDelayed(this, Math.max(200, timeout / 4));
            }
        };

        ui.postDelayed(silenceCheck, timeout);
    }

    private void disarmSilenceWatch() {
        if (silenceCheck != null) {
            ui.removeCallbacks(silenceCheck);
            silenceCheck = null;
        }
    }

    // ---------------------------------------------------------------- chrome

    private void setStatus(String text, int textColour, int backgroundColour) {
        statusText.setText(text);
        statusText.setTextColor(getColor(textColour));
        statusText.setBackgroundColor(getColor(backgroundColour));
    }

    /** Shows an alarm and clears it after {@link #ALARM_VISIBLE_MS}. */
    private void showAlarm(String title, String body) {
        alarmTitle.setText(title);
        alarmBody.setText(body);
        alarmPanel.setVisibility(View.VISIBLE);

        ui.removeCallbacks(clearAlarm);
        ui.postDelayed(clearAlarm, ALARM_VISIBLE_MS);
    }

    private void hideAlarm() {
        ui.removeCallbacks(clearAlarm);
        alarmPanel.setVisibility(View.GONE);
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

        static Row stray(String epc) {
            Row r = new Row();
            r.label = epc;
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
