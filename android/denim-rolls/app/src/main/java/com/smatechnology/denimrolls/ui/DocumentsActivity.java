package com.smatechnology.denimrolls.ui;

import android.content.Intent;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;
import androidx.swiperefreshlayout.widget.SwipeRefreshLayout;

import com.google.android.material.chip.Chip;
import com.google.android.material.chip.ChipGroup;
import com.smatechnology.denimrolls.R;
import com.smatechnology.denimrolls.data.ApiClient;
import com.smatechnology.denimrolls.data.AppSettings;
import com.smatechnology.denimrolls.data.DocumentSummary;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * The work list: documents this reader can act on.
 *
 * <p>Ordered with the workable ones first, because an operator standing at the
 * gate wants the job in front of them, not last week's completed paperwork.
 */
public final class DocumentsActivity extends AppCompatActivity {

    private final ExecutorService io = Executors.newSingleThreadExecutor();
    private final List<DocumentSummary> documents = new ArrayList<>();

    private ApiClient api;
    private AppSettings settings;
    private Adapter adapter;

    private SwipeRefreshLayout refresh;
    private TextView emptyView;
    private TextView userView;
    private TextView gateView;

    private String typeFilter;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_documents);

        api = new ApiClient(this);
        settings = new AppSettings(this);

        refresh = findViewById(R.id.refresh);
        emptyView = findViewById(R.id.empty);
        userView = findViewById(R.id.signed_in);
        gateView = findViewById(R.id.gate);

        RecyclerView list = findViewById(R.id.list);
        list.setLayoutManager(new LinearLayoutManager(this));
        adapter = new Adapter();
        list.setAdapter(adapter);

        refresh.setOnRefreshListener(this::load);

        ChipGroup filters = findViewById(R.id.filters);
        filters.setOnCheckedStateChangeListener((group, ids) -> {
            int id = ids.isEmpty() ? R.id.filter_all : ids.get(0);

            if (id == R.id.filter_inward) {
                typeFilter = "Inward";
            } else if (id == R.id.filter_outward) {
                typeFilter = "Outward";
            } else {
                typeFilter = null;
            }

            load();
        });

        findViewById(R.id.sign_out).setOnClickListener(v -> {
            api.signOut();
            startActivity(new Intent(this, LoginActivity.class));
            finish();
        });

        findViewById(R.id.settings).setOnClickListener(
                v -> startActivity(new Intent(this, SettingsActivity.class)));
    }

    @Override
    protected void onResume() {
        super.onResume();

        if (!api.isSignedIn()) {
            startActivity(new Intent(this, LoginActivity.class));
            finish();

            return;
        }

        userView.setText(getString(R.string.documents_signed_in, api.displayName()));
        gateView.setText(settings.gateCode());

        load();
    }

    private void load() {
        refresh.setRefreshing(true);

        io.execute(() -> {
            try {
                List<DocumentSummary> loaded = api.listDocuments(typeFilter, null);

                // Workable first, then most recent. The gate crew never wants
                // to scroll past finished paperwork to find today's job.
                loaded.sort((a, b) -> {
                    if (a.isWorkable() != b.isWorkable()) {
                        return a.isWorkable() ? -1 : 1;
                    }

                    return b.documentNumber.compareTo(a.documentNumber);
                });

                runOnUiThread(() -> {
                    documents.clear();
                    documents.addAll(loaded);
                    adapter.notifyDataSetChanged();
                    refresh.setRefreshing(false);
                    emptyView.setVisibility(documents.isEmpty() ? View.VISIBLE : View.GONE);
                });
            } catch (ApiClient.ApiException e) {
                runOnUiThread(() -> {
                    refresh.setRefreshing(false);
                    emptyView.setText(e.getMessage());
                    emptyView.setVisibility(View.VISIBLE);

                    if (!api.isSignedIn()) {
                        startActivity(new Intent(this, LoginActivity.class));
                        finish();
                    }
                });
            }
        });
    }

    private void open(DocumentSummary document) {
        Intent intent = new Intent(this, ScanActivity.class);
        intent.putExtra(ScanActivity.EXTRA_DOCUMENT_ID, document.id);
        startActivity(intent);
    }

    private final class Adapter extends RecyclerView.Adapter<Adapter.Holder> {

        @NonNull
        @Override
        public Holder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            return new Holder(LayoutInflater.from(parent.getContext())
                    .inflate(R.layout.item_document, parent, false));
        }

        @Override
        public void onBindViewHolder(@NonNull Holder holder, int position) {
            holder.bind(documents.get(position));
        }

        @Override
        public int getItemCount() {
            return documents.size();
        }

        final class Holder extends RecyclerView.ViewHolder {

            private final TextView number;
            private final TextView type;
            private final TextView status;
            private final TextView articles;
            private final TextView quantity;
            private final TextView balance;
            private final View stripe;

            Holder(@NonNull View view) {
                super(view);
                number = view.findViewById(R.id.number);
                type = view.findViewById(R.id.type);
                status = view.findViewById(R.id.status);
                articles = view.findViewById(R.id.articles);
                quantity = view.findViewById(R.id.quantity);
                balance = view.findViewById(R.id.balance);
                stripe = view.findViewById(R.id.stripe);
            }

            void bind(DocumentSummary d) {
                number.setText(d.documentNumber);
                type.setText(d.type.toUpperCase(Locale.US));
                status.setText(d.status);

                articles.setText(String.format(Locale.US, "%d", d.expectedArticles));
                quantity.setText(String.format(Locale.US, "%d", d.expectedQuantity));
                balance.setText(String.format(Locale.US, "%d", d.balanceArticles));

                int accent = d.isInward()
                        ? getColor(R.color.ok)
                        : getColor(R.color.info);

                stripe.setBackgroundColor(d.isWorkable() ? accent : getColor(R.color.line));
                type.setTextColor(accent);

                float alpha = d.isWorkable() ? 1f : 0.55f;
                itemView.setAlpha(alpha);

                itemView.setOnClickListener(v -> {
                    if (d.isWorkable()) {
                        open(d);
                    }
                });
            }
        }
    }

    @Override
    protected void onDestroy() {
        io.shutdownNow();
        super.onDestroy();
    }
}
