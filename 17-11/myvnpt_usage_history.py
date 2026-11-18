"""Request and display helpers for My VNPT's subscriber usage history.

The field names and date format follow MobileHistoryRequest/Response and
HistoryUsageViewModel in the My VNPT APK. No credentials or history are stored
by this module.
"""

import calendar
from datetime import date, datetime
from decimal import Decimal, InvalidOperation


HISTORY_ENDPOINT = '/mapi_v2/services/mobile_usage_history_detail'
HISTORY_OTP_ENDPOINT = '/mapi_v2/services/otp_send'
HISTORY_OTP_SERVICE = 'mobile_usage_history_detail'
CATEGORIES = (
    ('GPRS_2', 'Data'),
    ('Voice', 'Thoại'),
    ('SMS', 'SMS'),
    ('VAS', 'Dịch vụ'),
)


class UsageHistoryError(RuntimeError):
    pass


class HistoryOtpRequired(UsageHistoryError):
    pass


def recent_months(today=None):
    """The app offers the current month and the two preceding months."""
    today = today or date.today()
    months = []
    for offset in range(3):
        index = today.year * 12 + today.month - 1 - offset
        year, month_index = divmod(index, 12)
        month = month_index + 1
        months.append((f'Tháng {month:02d}/{year}', year, month))
    return months


def history_request(msisdn, session, year, month, otp=''):
    if not msisdn or not session:
        raise UsageHistoryError('Cần đăng nhập My VNPT trước khi tra cứu.')
    try:
        last_day = calendar.monthrange(int(year), int(month))[1]
    except (ValueError, TypeError) as exc:
        raise UsageHistoryError('Tháng tra cứu không hợp lệ.') from exc
    return {
        'msisdn': str(msisdn),
        'session': str(session),
        'otp': str(otp or '').strip(),
        'start_date': f'01{int(month):02d}{int(year):04d}',
        'end_date': f'{last_day:02d}{int(month):02d}{int(year):04d}',
    }


def parse_history_response(payload, http_status=200):
    if not isinstance(payload, dict):
        raise UsageHistoryError('My VNPT trả dữ liệu lịch sử sai định dạng.')
    code = str(payload.get('error_code', payload.get('errorCode', ''))).strip()
    message = str(payload.get('message') or '').strip()
    if http_status != 200:
        raise UsageHistoryError(
            f'Tra cứu lịch sử thất bại (HTTP {http_status}, mã {code or "?"}): '
            f'{message or "My VNPT từ chối yêu cầu."}'
        )
    if code == '1':
        raise HistoryOtpRequired(message or 'My VNPT trả mã 1.')
    if code != '0':
        raise UsageHistoryError(
            f'Tra cứu lịch sử thất bại (HTTP {http_status}, mã {code or "?"}): '
            f'{message or "My VNPT từ chối yêu cầu."}'
        )
    rows = payload.get('result') or []
    if not isinstance(rows, list):
        raise UsageHistoryError('Danh sách lịch sử sai định dạng.')
    totals = payload.get('usageToTal') or {}
    if not isinstance(totals, dict):
        totals = {}
    return {'rows': [row for row in rows if isinstance(row, dict)], 'totals': totals}


def _number(value):
    try:
        return Decimal(str(value or '0').replace(',', ''))
    except InvalidOperation:
        return Decimal(0)


def _date_for_row(row, category):
    value = str((row.get('DATE_CHARGE') if category == 'GPRS_2'
                 else row.get('CALL_DATE')) or '')
    for fmt in ('%Y%m%d', '%d/%m/%Y %H:%M:%S', '%d/%m/%Y %H:%M', '%d/%m/%Y'):
        try:
            return datetime.strptime(value, fmt)
        except ValueError:
            continue
    return None


def category_view(result, category, year, month):
    if category not in dict(CATEGORIES):
        raise UsageHistoryError('Loại lịch sử không hợp lệ.')
    rows = [row for row in result['rows'] if row.get('CALL_TYPE') == category]
    rows.sort(key=lambda row: _date_for_row(row, category) or datetime.min,
              reverse=True)
    days = calendar.monthrange(int(year), int(month))[1]
    daily = [0.0] * days
    charge = Decimal(0)
    usage = Decimal(0)
    for row in rows:
        charge += _number(row.get('CHARGE'))
        if category == 'GPRS_2':
            value = _number(row.get('VOLUME_CHARGE'))
        elif category == 'Voice':
            value = _number(row.get('DURATION'))
        elif category == 'SMS':
            value = Decimal(1)
        else:
            value = _number(row.get('CHARGE'))
        usage += value
        when = _date_for_row(row, category)
        if when and when.year == year and when.month == month:
            daily[when.day - 1] += float(value)
    if category == 'GPRS_2':
        main = f'{usage:,.0f} MB'
        label = 'Tổng Data'
    elif category == 'Voice':
        main = f'{len(rows):,} cuộc gọi · {int(usage) // 60} phút {int(usage) % 60} giây'
        label = 'Tổng cuộc gọi'
    elif category == 'SMS':
        main = f'{len(rows):,} tin nhắn'
        label = 'Tổng số tin nhắn'
    else:
        main = f'{charge:,.0f}đ'
        label = 'Tổng cước dịch vụ'
    change_key = {'GPRS_2': 'GPRS_2-Change', 'Voice': 'Voice-Change',
                  'SMS': 'SMS-Change', 'VAS': 'VAS-Change'}[category]
    return {
        'rows': rows,
        'daily': daily,
        'label': label,
        'main': main,
        'charge': f'{charge:,.0f}đ',
        'charge_label': {
            'GPRS_2': 'Tổng cước Data',
            'Voice': 'Tổng cước thoại',
            'SMS': 'Tổng cước SMS',
            'VAS': None,
        }[category],
        'change': result.get('totals', {}).get(change_key),
    }


def display_row(row, category):
    when = _date_for_row(row, category)
    date_text = when.strftime('%d/%m/%Y %H:%M') if when else str(
        row.get('DATE_CHARGE') or row.get('CALL_DATE') or '')
    charge = _number(row.get('CHARGE'))
    price = f'{charge:,.0f}đ'
    if category == 'GPRS_2':
        return (date_text, 'Data', f'{_number(row.get("VOLUME_CHARGE")):,.0f} MB', price)
    if category == 'Voice':
        seconds = int(_number(row.get('DURATION')))
        return (date_text, str(row.get('B_SUBS') or ''),
                f'{seconds // 60} phút {seconds % 60} giây', price)
    if category == 'SMS':
        return (date_text, str(row.get('B_SUBS') or ''), '1 tin nhắn', price)
    return (date_text, str(row.get('SERVICE_PROVIDER_ID') or row.get('SERVICE_TYPE') or ''),
            '', price)
