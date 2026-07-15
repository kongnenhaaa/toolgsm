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

## Concurrency theo COM

- Không có trần cứng 64 COM: backend tạo pipeline độc lập theo toàn bộ số COM thực tế.
- `BackendConcurrency` tự tăng mức ThreadPool theo số cổng được phát hiện; 64 là mức
  năng lực tối thiểu, không phải giới hạn tối đa.
- Mỗi COM có semaphore riêng để chuỗi lệnh AT/SMS/USSD không xen kẽ trên cùng modem.
- Khởi tạo modem, signal polling, balance, SMS sweep và các batch chạy đồng thời giữa
  các COM; lỗi hoặc timeout của một COM không khóa các COM còn lại.
- Reboot, Fix EC20, đổi SIM, xóa SMS, EZ COM, MyVNPT, chuyển tiếp cuộc gọi và Command
  Panel đều fan-out theo COM. COM đang bận chỉ bị bỏ qua riêng, không hủy cả batch.
- Bulk SMS chia thành một hàng đợi cho mỗi COM: tuần tự trong cùng COM nhưng các hàng
  đợi chạy đồng thời. Multipart SMS và registry phiên SIM cũng dùng khóa riêng theo COM.
- Các bước phụ thuộc trong cùng một workflow (ví dụ chuỗi AT cấu hình modem hoặc các
  lệnh trong một kịch bản) vẫn giữ đúng thứ tự; đây là tuần tự bắt buộc, không phải nút
  thắt toàn cục.

## Test doubles

Project `gsm.Tests` dùng `FakeGsmModemService`, không mở serial port và không cần EC20C.
Fake modem cho phép chặn một lệnh đang chạy, thay SIM/rút SIM tại đúng thời điểm
race-condition, sau đó xác minh backend không nhận kết quả của phiên cũ.

Chạy toàn bộ test:

```powershell
dotnet test gsm.Tests/gsm.Tests.csproj -c Debug
```
