package com.smatechnology.denimrolls.data;

import android.text.TextUtils;

import org.json.JSONObject;

/** One roll on a document: its tag, its stock code, and whether it has been read. */
public final class DocumentItem {

    public String epc = "";
    public String itemCode = "";
    public String itemName = "";
    public String productCode = "";
    public int quantity = 1;

    /** Confirmed by an earlier committed session. */
    public boolean isDetected;

    /** Read during the session running right now, not yet committed. */
    public boolean scannedNow;

    public static DocumentItem from(JSONObject json) {
        DocumentItem i = new DocumentItem();

        if (json == null) {
            return i;
        }

        i.epc = json.optString("epc", "");
        i.itemCode = json.optString("itemCode", "");
        i.itemName = json.optString("itemName", "");
        i.productCode = json.optString("productCode", "");
        i.quantity = json.optInt("quantity", 1);
        i.isDetected = json.optBoolean("isDetected");

        return i;
    }

    /** The stock code where there is one, otherwise the tag itself. */
    public String label() {
        return TextUtils.isEmpty(itemCode) ? epc : itemCode;
    }

    public boolean isAccountedFor() {
        return isDetected || scannedNow;
    }
}
