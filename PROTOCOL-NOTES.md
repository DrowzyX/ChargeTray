# Примечания к протоколам

- Razer: форматы и USB‑ID сверены с публично описанными данными OpenRazer: [идентификаторы](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.h), [параметры запроса](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.c). В ChargeTray используется собственный опрос через Windows HID.
- AULA F108 Pro: исходный запрос батареи описан [здесь](https://github.com/meitar/aula-driver/blob/main/Sources/AulaKit/Hardware/WirelessAulaDevice.swift). Положение поля заряда и контрольная сумма проверены по записи реального Windows‑ответа пользовательского ресивера. Регрессионный тест содержит фиксированный обезличенный пакет.
