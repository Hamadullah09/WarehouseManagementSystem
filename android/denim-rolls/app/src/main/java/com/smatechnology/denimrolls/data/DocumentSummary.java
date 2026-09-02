package com.smatechnology.denimrolls.data;

import org.json.JSONObject;

/** A document as it appears in the picking list. */
public class DocumentSummary {

    public int id;
    public String documentNumber = "";
    public String type = "";
    public String status = "";
    public String userDisplayName = "";
    public String gateCode = "";
    public int expectedArticles;
    public int detectedArticles;
    public int balanceArticles;
    public int expectedQuantity;
    public int detectedQuantity;
    public int balanceQuantity;

    public static DocumentSummary from(JSONObject json) {
        DocumentSummary d = new DocumentSummary();
        apply(d, json);
        return d;
    }

    protected static void apply(DocumentSummary d, JSONObject json) {
        if (json == null) {
            return;
        }

        d.id = json.optInt("id");
        d.documentNumber = json.optString("documentNumber", "");
        d.type = json.optString("type", "");
        d.status = json.optString("status", "");
        d.userDisplayName = json.optString("userDisplayName", "");
        d.gateCode = json.optString("gateCode", "");
        d.expectedArticles = json.optInt("expectedArticles");
        d.detectedArticles = json.optInt("detectedArticles");
        d.balanceArticles = json.optInt("balanceArticles");
        d.expectedQuantity = json.optInt("expectedQuantity");
        d.detectedQuantity = json.optInt("detectedQuantity");
        d.balanceQuantity = json.optInt("balanceQuantity");
    }

    public boolean isInward() {
        return "Inward".equalsIgnoreCase(type);
    }

    /** True while the document still has work outstanding. */
    public boolean isWorkable() {
        return !"Completed".equalsIgnoreCase(status) && !"Cancelled".equalsIgnoreCase(status);
    }
}
