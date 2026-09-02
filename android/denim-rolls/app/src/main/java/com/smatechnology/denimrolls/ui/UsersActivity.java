package com.smatechnology.denimrolls.ui;

import android.os.Bundle;
import android.text.InputType;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.smatechnology.denimrolls.R;
import com.smatechnology.denimrolls.data.ApiClient;
import com.smatechnology.denimrolls.data.WarehouseUser;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Account administration, for administrators only.
 *
 * <p>An account that has signed documents is switched off rather than removed,
 * so the audit trail keeps naming a real person. The server decides which of
 * the two happens and says so in its reply; this screen repeats that rather
 * than guessing.
 */
public final class UsersActivity extends AppCompatActivity {

    private final ExecutorService io = Executors.newSingleThreadExecutor();
    private final List<WarehouseUser> users = new ArrayList<>();
    private final List<String> roles = new ArrayList<>();

    private ApiClient api;
    private Adapter adapter;
    private TextView emptyView;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_users);

        api = new ApiClient(this);
        emptyView = findViewById(R.id.empty);

        RecyclerView list = findViewById(R.id.list);
        list.setLayoutManager(new LinearLayoutManager(this));
        adapter = new Adapter();
        list.setAdapter(adapter);

        findViewById(R.id.add).setOnClickListener(v -> edit(null));
        findViewById(R.id.back).setOnClickListener(v -> finish());

        load();
    }

    private void load() {
        io.execute(() -> {
            try {
                List<WarehouseUser> loaded = api.listUsers();
                List<String> loadedRoles = api.listRoles();

                runOnUiThread(() -> {
                    users.clear();
                    users.addAll(loaded);
                    roles.clear();
                    roles.addAll(loadedRoles);
                    adapter.notifyDataSetChanged();
                    emptyView.setVisibility(users.isEmpty() ? View.VISIBLE : View.GONE);
                });
            } catch (ApiClient.ApiException e) {
                runOnUiThread(() -> {
                    emptyView.setText(e.getMessage());
                    emptyView.setVisibility(View.VISIBLE);
                });
            }
        });
    }

    /** Adds when user is null, otherwise edits. */
    private void edit(@Nullable WarehouseUser user) {
        View form = LayoutInflater.from(this).inflate(R.layout.dialog_user, null);

        EditText userName = form.findViewById(R.id.username);
        EditText displayName = form.findViewById(R.id.display_name);
        EditText email = form.findViewById(R.id.email);
        EditText password = form.findViewById(R.id.password);
        Spinner role = form.findViewById(R.id.role);
        LinearLayout passwordRow = form.findViewById(R.id.password_row);

        role.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, roles));

        if (user != null) {
            userName.setText(user.userName);
            userName.setEnabled(false);
            displayName.setText(user.displayName);
            email.setText(user.email);

            // An existing password is never shown, and is reset separately.
            passwordRow.setVisibility(View.GONE);

            int index = roles.indexOf(user.role);

            if (index >= 0) {
                role.setSelection(index);
            }
        }

        new AlertDialog.Builder(this)
                .setTitle(user == null ? R.string.add_user : R.string.edit)
                .setView(form)
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.save_user, (d, w) -> {
                    String chosen = roles.isEmpty() ? "Operator" : (String) role.getSelectedItem();

                    io.execute(() -> {
                        try {
                            if (user == null) {
                                api.createUser(
                                        userName.getText().toString().trim(),
                                        displayName.getText().toString().trim(),
                                        email.getText().toString().trim(),
                                        password.getText().toString(),
                                        chosen);
                            } else {
                                api.updateUser(
                                        user.id,
                                        displayName.getText().toString().trim(),
                                        email.getText().toString().trim(),
                                        chosen,
                                        user.isActive);
                            }

                            runOnUiThread(this::load);
                        } catch (ApiClient.ApiException e) {
                            runOnUiThread(() -> toast(e.getMessage()));
                        }
                    });
                })
                .show();
    }

    private void resetPassword(WarehouseUser user) {
        EditText input = new EditText(this);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        input.setHint(R.string.new_password);
        input.setTextSize(19f);

        new AlertDialog.Builder(this)
                .setTitle(getString(R.string.reset_password_for, user.displayName))
                .setView(input)
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.save_user, (d, w) -> io.execute(() -> {
                    try {
                        api.resetUserPassword(user.id, input.getText().toString());

                        runOnUiThread(() -> {
                            toast(getString(R.string.password_set_for, user.displayName));
                            load();
                        });
                    } catch (ApiClient.ApiException e) {
                        runOnUiThread(() -> toast(e.getMessage()));
                    }
                }))
                .show();
    }

    private void remove(WarehouseUser user) {
        new AlertDialog.Builder(this)
                .setMessage(getString(R.string.remove_user_confirm, user.displayName))
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.delete, (d, w) -> io.execute(() -> {
                    try {
                        String message = api.deleteUser(user.id);

                        runOnUiThread(() -> {
                            toast(message);
                            load();
                        });
                    } catch (ApiClient.ApiException e) {
                        runOnUiThread(() -> toast(e.getMessage()));
                    }
                }))
                .show();
    }

    private void toast(String message) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
    }

    @Override
    protected void onDestroy() {
        io.shutdownNow();
        super.onDestroy();
    }

    private final class Adapter extends RecyclerView.Adapter<Adapter.Holder> {

        @NonNull
        @Override
        public Holder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            return new Holder(LayoutInflater.from(parent.getContext())
                    .inflate(R.layout.item_user, parent, false));
        }

        @Override
        public void onBindViewHolder(@NonNull Holder holder, int position) {
            holder.bind(users.get(position));
        }

        @Override
        public int getItemCount() {
            return users.size();
        }

        final class Holder extends RecyclerView.ViewHolder {

            private final TextView name;
            private final TextView detail;
            private final TextView status;

            Holder(@NonNull View view) {
                super(view);
                name = view.findViewById(R.id.name);
                detail = view.findViewById(R.id.detail);
                status = view.findViewById(R.id.status);

                view.findViewById(R.id.edit).setOnClickListener(v -> withRow(UsersActivity.this::edit));
                view.findViewById(R.id.reset).setOnClickListener(v -> withRow(UsersActivity.this::resetPassword));
                view.findViewById(R.id.remove).setOnClickListener(v -> withRow(UsersActivity.this::remove));
            }

            /** Guards against a row action firing after the list has moved on. */
            private void withRow(java.util.function.Consumer<WarehouseUser> action) {
                int position = getBindingAdapterPosition();

                if (position >= 0 && position < users.size()) {
                    action.accept(users.get(position));
                }
            }

            void bind(WarehouseUser u) {
                name.setText(u.displayName);
                detail.setText(getString(R.string.user_detail_line, u.userName, u.role));
                status.setText(u.statusLine());

                int colour = !u.isActive
                        ? R.color.ink_muted
                        : u.isLockedOut || u.resetRequested ? R.color.warn : R.color.ok;

                status.setBackgroundColor(getColor(colour));
            }
        }
    }
}
