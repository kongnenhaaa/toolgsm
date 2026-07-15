# Backend GSM architecture

## Runtime boundaries

- `IGsmModemService`: transport thấp nhất, chỉ biết serial/AT/audio.
- `IPortSessionRegistry`: sở hữu CCID, epoch và cancellation token theo từng COM.
- `IGsmSmsService`: lock riêng theo COM, retry SMS và khôi phục charset.
- `IGsmUssdService`: preflight SIM/network/signal, throttle và retry USSD.
- `IGsmCallService`: gọi theo token phiên SIM; rút SIM sẽ hủy call.
- `IGsmBackgroundSupervisor`: sở hữu watchdog, signal polling, balance refresh và SMS sweep.
- `MainViewModel`: điều phối UI, collection và thông báo; không còn tự tạo modem.

Mọi operation bắt đầu bằng `PortSessionLease`. Kết quả chỉ hợp lệ nếu
`PortName + CCID + Epoch` vẫn trùng khớp khi operation kết thúc.

## Test doubles

Project `gsm.Tests` dùng `FakeGsmModemService`, không mở serial port và không cần EC20C.
Fake modem cho phép chặn một lệnh đang chạy, thay SIM/rút SIM tại đúng thời điểm
race-condition, sau đó xác minh backend không nhận kết quả của phiên cũ.

Chạy toàn bộ test:

```powershell
dotnet test gsm.Tests/gsm.Tests.csproj -c Debug
```
