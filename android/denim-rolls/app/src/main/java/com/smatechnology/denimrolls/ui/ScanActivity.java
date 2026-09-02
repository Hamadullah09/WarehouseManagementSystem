package com.smatechnology.denimrolls.ui;

import android.os.Bundle;
import android.os.SystemClock;
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
 * The gate screen: one document, the reader, and a verdict.
 *
 * <p>Reads are checked against the document as they arrive so the operator is
 * told immediately when a roll does not belong — while the load is still in
 * front of them and something can be done about it. That check is a courtesy,
 * not the decision: on stop the whole session goes to the server, which
 * re-validates against the database and is the only thing that moves stock.
 *
 * <p>Laid out dark and large. This runs on a panel above a loading bay, not on
 * a desk.
 */
public final class ScanActivity extends AppCompatActivity implements ReaderController.Listener {

    public static final String EXTRA_DOCUMENT_ID = "document_id";

    private final ExecutorService io = Executors.newSingleThreadExecutor();

    /** Distinct tags accepted this session, in the order they arrived. */
    private final Set<String> accepted = new LinkedHashSet<>();

    private final List<Row> rows = new ArrayList<>();

    private ApiClient api;
    private AppSettings settings;
    private ReaderController reader;
    private DocumentDetail document;

    private final android.os.Handler watchdog = new android.os.Handler(android.os.Looper.getMainLooper());
    private Runnable silenceCheck;
    private long lastReadAt;
    private boolean silenceReported;

    private int documentId;
    private long sessionStartedAt;
    private String sessionKey;
    private boolean sessionHadAlarm;

    private TextView documentNumber;
    private TextView movement;
    private TextView userName;
    private TextView totalArticles;
    private TextView totalQuantity;
    private TextView totalsLine;
    private TextView balanceArticles;
    private TextView balanceQuantity;
    private TextView statusText;
    private TextView lastEpc;
    private View statusPanel;
    private View alarmPanel;
    private TextView alarmTitle;
    private TextView alarmBody;
    private MaterialButton startButton;
    private MaterialButton stopButton;
    private TextView progressText;
    private com.google.android.material.progressindicator.LinearProgressIndicator progressBar;
    private TextView controlHelp;
    private RowAdapter adapter;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        setTheme(R.style.Theme_DenimRolls_Scan);
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_scan);

        api = new ApiClient(this);
        settings = new AppSettings(this);
        documentId = getIntent().getIntExtra(EXTRA_DOCUMENT_ID, 0);

        bindViews();

        reader = new ReaderController(this);
        reader.setListener(this);

        startButton.setOnClickListener(v -> startSession());
        stopButton.setOnClickListener(v -> confirmStop());
        findViewById(R.id.close).setOnClickListener(v -> finish());

        loadDocument();
    }

    private void bindViews() {
        documentNumber = findViewById(R.id.document_number);
        movement = findViewById(R.id.movement);
        userName = findViewById(R.id.user_name);
        totalArticles = findViewById(R.id.total_articles);
        totalQuantity = findViewById(R.id.total_quantity);   // portrait only
        totalsLine = findViewById(R.id.totals_line);         // landscape only
        balanceArticles = findViewById(R.id.balance_articles);
        balanceQuantity = findViewById(R.id.balance_quantity);
        statusText = findViewById(R.id.status_text);
        statusPanel = findViewById(R.id.status_panel);
        lastEpc = findViewById(R.id.last_epc);
        alarmPanel = findViewById(R.id.alarm_panel);
        alarmTitle = findViewById(R.id.alarm_title);
        alarmBody = findViewById(R.id.alarm_body);
        startButton = findViewById(R.id.start);
        stopButton = findViewById(R.id.stop);
        progressText = findViewById(R.id.progress_text);
        progressBar = findViewById(R.id.progress_bar);
        controlHelp = findViewById(R.id.control_help); // absent in landscape

        findViewById(R.id.alarm_dismiss).setOnClickListener(v -> hideAlarm());

        RecyclerView list = findViewById(R.id.rolls);
        list.setLayoutManager(new LinearLayoutManager(this));
        adapter = new RowAdapter();
        list.setAdapter(adapter);

        userName.setText(api.displayName());
    }

    // ------------------------------------------------------------- document

    private void loadDocument() {
        setStatus(getString(R.string.scan_loading), R.color.scan_muted);

        io.execute(() -> {
            try {
                DocumentDetail loaded = api.getDocument(documentId);

                runOnUiThread(() -> {
                    document = loaded;
                    renderDocument();

                    // Opening the module here rather than on first press keeps
                    // the delay off the critical path at the gate.
                    io.execute(() -> reader.initialise());
                });
            } catch (ApiClient.ApiException e) {
                runOnUiThread(() -> showAlarm(getString(R.string.scan_load_failed), e.getMessage()));
            }
        });
    }

    private void renderDocument() {
        documentNumber.setText(document.documentNumber);
        movement.setText(document.type.toUpperCase(Locale.US));
        movement.setBackgroundTintList(android.content.res.ColorStateList.valueOf(
                getColor(document.isInward() ? R.color.ok : R.color.info)));

        showTotals();

        rebuildRows();
        updateBalances();

        setStatus(getString(R.string.scan_ready), R.color.scan_text);
        startButton.setEnabled(true);
    }

    /** Landscape shows totals on the header line; portrait gives them their own tiles. */
    private void showTotals() {
        if (totalsLine != null) {
            totalsLine.setText(getString(R.string.scan_totals_line,
                    document.expectedArticles, document.expectedQuantity));
        }

        if (totalArticles != null) {
            totalArticles.setText(String.valueOf(document.expectedArticles));
        }

        if (totalQuantity != null) {
            totalQuantity.setText(String.valueOf(document.expectedQuantity));
        }
    }

    private void rebuildRows() {
        rows.clear();

        for (DocumentItem item : document.items) {
            rows.add(Row.of(item));
        }

        adapter.notifyDataSetChanged();
    }

    private void updateBalances() {
        int outstanding = document.outstanding();
        int total = document.items.size();
        int done = total - outstanding;

        balanceArticles.setText(String.valueOf(outstanding));
        balanceQuantity.setText(String.valueOf(document.outstandingQuantity()));

        progressText.setText(getString(R.string.scan_progress, done, total));
        progressBar.setProgress(total == 0 ? 0 : (done * 100) / total);
    }

    // -------------------------------------------------------------- session

    private void startSession() {
        if (document == null) {
            return;
        }

        accepted.clear();
        sessionHadAlarm = false;
        sessionKey = UUID.randomUUID().toString();
        sessionStartedAt = System.currentTimeMillis();

        for (DocumentItem item : document.items) {
            item.scannedNow = false;
        }

        rebuildRows();
        updateBalances();
        hideAlarm();

        if (!reader.start()) {
            return;
        }

        startButton.setVisibility(View.GONE);
        stopButton.setVisibility(View.VISIBLE);
        if (controlHelp != null) {
            controlHelp.setText(R.string.scan_help_stop);
        }

        setStatus(getString(R.string.scan_reading), R.color.info);
        armSilenceWatchdog();
    }

    private void confirmStop() {
        new AlertDialog.Builder(this)
                .setMessage(R.string.scan_confirm_stop)
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.stop, (d, w) -> stopSession())
                .show();
    }

    private void stopSession() {
        disarmSilenceWatchdog();
        reader.stop();

        startButton.setVisibility(View.VISIBLE);
        stopButton.setVisibility(View.GONE);
        startButton.setEnabled(false);
        if (controlHelp != null) {
            controlHelp.setText(R.string.scan_help_start);
        }

        setStatus(getString(R.string.scan_stopped), R.color.warn);

        // Nothing read at all means something may have gone through untagged.
        if (accepted.isEmpty()) {
            reader.signalAlarm();
            showAlarm(getString(R.string.scan_invalid_title), getString(R.string.scan_no_epc));
        }

        submitSession();
    }

    /**
     * Warns when nothing has been read for the configured window.
     *
     * <p>A roll going past without a readable tag looks exactly like silence,
     * so silence is what gets watched. It fires once per quiet spell rather
     * than every tick: an operator who has already been told does not need
     * telling four times a second, and a latched beacon tells them nothing new.
     */
    private void armSilenceWatchdog() {
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

                long quiet = System.currentTimeMillis() - lastReadAt;

                if (quiet >= timeout && !silenceReported) {
                    silenceReported = true;
                    sessionHadAlarm = true;

                    reader.signalAlarm();
                    showAlarm(getString(R.string.scan_no_tag_title), getString(R.string.scan_no_epc));
                }

                watchdog.postDelayed(this, Math.max(200, timeout / 4));
            }
        };

        watchdog.postDelayed(silenceCheck, timeout);
    }

    private void disarmSilenceWatchdog() {
        if (silenceCheck != null) {
            watchdog.removeCallbacks(silenceCheck);
            silenceCheck = null;
        }
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
            setStatus(getString(R.string.scan_pass), R.color.ok);
        } else {
            reader.signalAlarm();
            setStatus(result.summary, R.color.alarm);
        }

        // Reload so the committed state on screen is the server's, not ours.
        loadDocumentQuietly();
    }

    private void loadDocumentQuietly() {
        io.execute(() -> {
            try {
                DocumentDetail loaded = api.getDocument(documentId);

                runOnUiThread(() -> {
                    document = loaded;
                    documentNumber.setText(document.documentNumber);
                    showTotals();
                    rebuildRows();
                    updateBalances();
                });
            } catch (ApiClient.ApiException ignored) {
                // The verdict has already been shown; a refresh failure here is
                // not worth a second error in front of the operator.
            }
        });
    }

    // -------------------------------------------------- ReaderController hooks

    @Override
    public void onReaderReady(String version) {
        // Nothing to show: readiness is implied by START being enabled.
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

    @Override
    public void onEpcAccepted(String epc, String rssi, String antenna) {
        if (document == null) {
            return;
        }

        lastEpc.setText(epc);
        lastReadAt = System.currentTimeMillis();
        silenceReported = false;

        if (!accepted.add(epc)) {
            return;
        }

        DocumentItem item = document.findByEpc(epc);

        if (item == null) {
            // On the floor the distinction that matters is "not on this
            // document"; whether the tag is unknown to the warehouse entirely
            // is the server's call and comes back with the verdict.
            sessionHadAlarm = true;
            reader.signalAlarm();
            showAlarm(getString(R.string.scan_invalid_title),
                    getString(R.string.scan_invalid_body, document.documentNumber));

            rows.add(0, Row.stray(epc));
            adapter.notifyItemInserted(0);

            return;
        }

        item.scannedNow = true;
        reader.signalAccepted();

        if (!sessionHadAlarm) {
            hideAlarm();
        }

        rebuildRows();
        updateBalances();
    }

    // ----------------------------------------------------------------- chrome

    private void setStatus(String text, int colourRes) {
        statusText.setText(text);
        statusText.setTextColor(getColor(colourRes));
    }

    private void showAlarm(String title, String body) {
        alarmTitle.setText(title);
        alarmBody.setText(body);
        alarmPanel.setVisibility(View.VISIBLE);
    }

    private void hideAlarm() {
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
        disarmSilenceWatchdog();
        reader.release();
        io.shutdownNow();
        super.onDestroy();
    }

    // ------------------------------------------------------------------ rows

    /** A line on screen: either a roll from the document, or a stray tag. */
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
            private final View stripe;

            Holder(@NonNull View view) {
                super(view);
                code = view.findViewById(R.id.code);
                tag = view.findViewById(R.id.tag);
                state = view.findViewById(R.id.state);
                stripe = view.findViewById(R.id.stripe);
            }

            void bind(Row row) {
                code.setText(row.label);
                tag.setText(row.epc);

                if (row.stray) {
                    state.setText(R.string.scan_invalid_title);
                    state.setTextColor(getColor(R.color.alarm));
                    stripe.setBackgroundColor(getColor(R.color.alarm));
                } else if (row.scanned) {
                    state.setText(R.string.scan_state_read);
                    state.setTextColor(getColor(R.color.ok));
                    stripe.setBackgroundColor(getColor(R.color.ok));
                } else if (row.committed) {
                    state.setText(R.string.scan_state_done);
                    state.setTextColor(getColor(R.color.scan_muted));
                    stripe.setBackgroundColor(getColor(R.color.scan_muted));
                } else {
                    state.setText(R.string.scan_state_pending);
                    state.setTextColor(getColor(R.color.scan_muted));
                    stripe.setBackgroundColor(getColor(R.color.scan_line));
                }
            }
        }
    }
}
