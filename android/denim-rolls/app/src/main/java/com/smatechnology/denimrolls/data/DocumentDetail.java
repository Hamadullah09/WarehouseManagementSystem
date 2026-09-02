package com.smatechnology.denimrolls.data;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/** A document with every roll it expects. */
public final class DocumentDetail extends DocumentSummary {

    public String reference = "";
    public String notes = "";
    public final List<DocumentItem> items = new ArrayList<>();

    public static DocumentDetail from(JSONObject json) {
        DocumentDetail d = new DocumentDetail();
        apply(d, json);

        if (json == null) {
            return d;
        }

        d.reference = json.optString("reference", "");
        d.notes = json.optString("notes", "");

        JSONArray array = json.optJSONArray("items");

        for (int i = 0; array != null && i < array.length(); i++) {
            d.items.add(DocumentItem.from(array.optJSONObject(i)));
        }

        return d;
    }

    /** Rolls still to be read. */
    public int outstanding() {
        int n = 0;

        for (DocumentItem item : items) {
            if (!item.isAccountedFor()) {
                n++;
            }
        }

        return n;
    }

    public int outstandingQuantity() {
        int n = 0;

        for (DocumentItem item : items) {
            if (!item.isAccountedFor()) {
                n += item.quantity;
            }
        }

        return n;
    }

    /** Rolls read during this session but not yet committed. */
    public int scannedNow() {
        int n = 0;

        for (DocumentItem item : items) {
            if (item.scannedNow) {
                n++;
            }
        }

        return n;
    }

    /** Finds a roll by tag, or null when the tag is not on this document. */
    public DocumentItem findByEpc(String epc) {
        if (epc == null) {
            return null;
        }

        for (DocumentItem item : items) {
            if (item.epc.equalsIgnoreCase(epc)) {
                return item;
            }
        }

        return null;
    }
}
