// ToolGSM Google Sheets gateway.
// Replace this value with the "Mã bảo mật đồng bộ" shown in ToolGSM Settings.
const SHARED_SECRET = 'PASTE_THE_SAME_SECRET_FROM_TOOLGSM_HERE';

function doPost(e) {
  const lock = LockService.getScriptLock();
  try {
    if (!e || !e.postData || !e.postData.contents) {
      return jsonResponse({ ok: false, message: 'Payload trống.' });
    }

    const request = JSON.parse(e.postData.contents);
    if (!SHARED_SECRET || request.secret !== SHARED_SECRET) {
      return jsonResponse({ ok: false, message: 'Mã bảo mật không đúng.' });
    }

    if (String(request.action || '').toLowerCase() === 'ping') {
      return jsonResponse({
        ok: true,
        rows: 0,
        message: 'Kết nối Apps Script thành công.'
      });
    }

    const spreadsheetId = String(request.spreadsheetId || '').trim();
    if (!/^[A-Za-z0-9_-]{20,}$/.test(spreadsheetId)) {
      return jsonResponse({ ok: false, message: 'Spreadsheet ID không hợp lệ.' });
    }

    let worksheetName = String(request.worksheetName || 'ToolGSM').trim();
    if (!worksheetName) worksheetName = 'ToolGSM';
    if (worksheetName.length > 100 || /[\[\]:*?\/\\]/.test(worksheetName)) {
      return jsonResponse({ ok: false, message: 'Tên tab Google Sheet không hợp lệ.' });
    }

    const columns = Array.isArray(request.columns) ? request.columns : [];
    const rows = Array.isArray(request.rows) ? request.rows : [];
    if (!columns.length) {
      return jsonResponse({ ok: false, message: 'Không có cột dữ liệu.' });
    }

    const width = columns.length;
    if (rows.some(row => !Array.isArray(row) || row.length !== width)) {
      return jsonResponse({ ok: false, message: 'Dữ liệu không khớp số cột.' });
    }

    if (!lock.tryLock(30000)) {
      return jsonResponse({ ok: false, message: 'Gateway đang bận, vui lòng thử lại.' });
    }

    const spreadsheet = SpreadsheetApp.openById(spreadsheetId);
    let sheet = spreadsheet.getSheetByName(worksheetName);
    if (!sheet) sheet = spreadsheet.insertSheet(worksheetName);

    const filter = sheet.getFilter();
    if (filter) filter.remove();
    sheet.getBandings().forEach(banding => banding.remove());
    sheet.clear();

    const header = columns.map(column => plainText(column.header || column.key || ''));
    const values = [header].concat(rows.map(row => row.map(plainText)));
    const range = sheet.getRange(1, 1, values.length, width);
    range.setNumberFormat('@');
    range.setValues(values);
    formatToolGsmSheet(sheet, range, columns, rows.length);

    const now = new Date();
    PropertiesService.getScriptProperties().setProperty(
      'lastToolGsmSync',
      JSON.stringify({
        spreadsheetId: spreadsheetId,
        worksheetName: worksheetName,
        machineId: String(request.machineId || ''),
        rows: rows.length,
        syncedAt: now.toISOString()
      })
    );

    return jsonResponse({
      ok: true,
      rows: rows.length,
      message: 'Đã đồng bộ ' + rows.length + ' dòng lên "' + worksheetName + '".'
    });
  } catch (error) {
    return jsonResponse({
      ok: false,
      message: error && error.message ? error.message : String(error)
    });
  } finally {
    if (lock.hasLock()) lock.releaseLock();
  }
}

function plainText(value) {
  const text = value === null || value === undefined ? '' : String(value);
  return /^[=+\-@]/.test(text) ? "'" + text : text;
}

function formatToolGsmSheet(sheet, range, columns, rowCount) {
  const width = columns.length;
  range
    .setFontFamily('Arial')
    .setFontSize(10)
    .setVerticalAlignment('middle')
    .setWrap(true)
    .setBorder(
      true, true, true, true, true, true,
      '#D1D5DB',
      SpreadsheetApp.BorderStyle.SOLID
    );

  range.applyRowBanding(SpreadsheetApp.BandingTheme.LIGHT_GREY, true, false);

  const headerRange = sheet.getRange(1, 1, 1, width);
  headerRange
    .setFontWeight('bold')
    .setFontSize(10)
    .setHorizontalAlignment('center')
    .setVerticalAlignment('middle')
    .setWrap(true)
    .setBackground('#1D4ED8')
    .setFontColor('#FFFFFF');

  const columnWidths = {
    Stt: 55,
    PortName: 85,
    Status: 140,
    NetworkProvider: 110,
    NetworkType: 90,
    Signal: 110,
    LastSignalScan: 155,
    Balance: 100,
    PhoneNumber: 125,
    SimType: 95,
    Imei: 155,
    Serial: 190,
    ExpiryDate: 110,
    SimRegDate: 125,
    Lock1C: 95,
    Lock2C: 95,
    ForwardedTo: 130,
    VnptStatus: 165,
    Otp: 90,
    LastSmsSender: 145,
    LastMessageContent: 360
  };
  columns.forEach((column, index) => {
    const key = String(column.key || '');
    sheet.setColumnWidth(index + 1, columnWidths[key] || 120);
  });

  if (rowCount > 0) {
    const bodyRange = sheet.getRange(2, 1, rowCount, width);
    bodyRange.setVerticalAlignment('middle').setWrap(true);

    const centeredKeys = new Set([
      'Stt', 'PortName', 'Status', 'NetworkProvider', 'NetworkType',
      'Signal', 'LastSignalScan', 'Balance', 'PhoneNumber', 'SimType',
      'Imei', 'Serial', 'ExpiryDate', 'SimRegDate', 'Lock1C', 'Lock2C',
      'ForwardedTo', 'VnptStatus', 'Otp', 'LastSmsSender'
    ]);
    columns.forEach((column, index) => {
      if (centeredKeys.has(String(column.key || ''))) {
        sheet.getRange(2, index + 1, rowCount, 1)
          .setHorizontalAlignment('center');
      }
    });
  }

  sheet.setFrozenRows(1);
  sheet.setFrozenColumns(Math.min(2, width));
  sheet.autoResizeRows(1, rowCount + 1);
  sheet.setRowHeight(1, 44);
  if (rowCount > 0) range.createFilter();
}

function jsonResponse(value) {
  return ContentService
    .createTextOutput(JSON.stringify(value))
    .setMimeType(ContentService.MimeType.JSON);
}
