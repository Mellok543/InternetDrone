Mell DroneLab — Internet Drone Control VPS v2

Архитектура:
1) OperatorStation — WPF программа на Windows.
   Пульт -> калибровка -> каналы -> UDP на VPS.

2) VpsRelay — консольная программа на VPS.
   Принимает оператора и дрон по room.
   Пересылает TO_DRONE операторских пакетов на Raspberry Pi.
   Пересылает TO_OPERATOR телеметрии/ACK обратно оператору.

3) DroneReceiver — консольная программа на Raspberry Pi.
   Подключается к VPS как drone.
   Принимает команды, показывает каналы.
   MAVLink в ArduPilot будет следующим этапом.

Порты:
- UDP 50555 на VPS должен быть открыт.
- OperatorStation и DroneReceiver должны использовать один и тот же Room.

Запуск VPS:
dotnet run --project VpsRelay -- 50555

UFW на VPS:
sudo ufw allow 50555/udp

Запуск Raspberry Pi:
dotnet run --project DroneReceiver -- VPS_IP 50555 mell-drone

Запуск Windows:
Открыть OperatorStation/OperatorStation.csproj в Visual Studio 2022.
Ввести VPS IP, порт 50555, room mell-drone.
Сделать калибровку.
Нажать CONNECT.

Важно:
- Это пока контур связи и операторский интерфейс.
- В ArduPilot ещё ничего не отправляется.
- Следующий этап: DroneReceiver -> MAVLink RC Override / mode commands / failsafe.
- Все тесты управления только без пропеллеров.
