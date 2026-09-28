# Dynamic Island для Windows (iOS 26 Liquid Glass)

🏝️ **Dynamic Island** — повноцінний інтерактивний острівець для Windows у стилі Apple iOS з лаунчером **iOS 26 Liquid Glass**, нативним шрифтом **SF Pro Rounded Bold**, анімаціями 60 FPS та підтримкою віджетів.

---

## ✨ Основні можливості

- 🎵 **Smart Media Player**:
  - Інтеграція з Windows SMTC (Spotify, YouTube, Яндекс Музика, браузери тощо).
  - Живий аудіо-еквалайзер / спектр звуку.
  - Обкладинки альбомів, плавний скролінг назв (marquee), керування треками (Shuffle, Prev, Play/Pause, Next, Repeat, Scrubber прогресу).

- 📞 **Інтеграція дзвінків Telegram**:
  - Відповідь на дзвінок та відхилення з острова.
  - Керування мікрофоном (Mute/Unmute) та кнопка завершення дзвінка.
  - Живий таймер тривалості розмови.

- 🔔 **iOS Центр сповіщень**:
  - Компактні спливаючі повідомлення Windows у стилі iOS без нав'язливих тостів.
  - Аватарки додатків (Telegram, Discord та системні).

- 🚀 **iOS 26 Liquid Glass Launcher**:
  - Окремий майстер встановлення (Wizard): *Ласкаво просимо → Установка → Налаштування*.
  - Автоматичне завантаження компонентів прямо з GitHub.
  - Налаштування в реальному часі:
    - **Розміщення**: Ліворуч / По центру / Праворуч.
    - **Відступ від верху**: 0 – 50 px.
    - **Масштаб острова**: 80% – 130%.
    - **Тема**: Темна (OLED), Світла, Системна + 5 акцентних кольорів.
    - **Автозапуск разом із Windows**.

- 🔤 **Справжній Apple SF Pro Rounded Bold**:
  - Повні TrueType контури для ідеального рендерингу в Windows (Regular, Medium, Semibold, Bold, Heavy, Black).

---

## 🛠️ Встановлення

1. Завантажте `DynamicIslandLauncher.exe` або `dynamic-island-bundle.zip`.
2. Запустіть лаунчер — він автоматично завантажить та розгорне компоненти в `%AppData%\DynamicIsland\`.
3. Налаштуйте острівець під себе через 3-й крок лаунчера.

---

## 💻 Збірка з вихідного коду (.NET 8)

```bash
# Збірка острова
dotnet build "dynamic ayland.csproj" -c Release

# Збірка лаунчера
dotnet build "Launcher/DynamicIslandLauncher.csproj" -c Release

# Публікація в папку publish
dotnet publish "dynamic ayland.csproj" -c Release -r win-x64 --self-contained false -o "./publish"
dotnet publish "Launcher/DynamicIslandLauncher.csproj" -c Release -r win-x64 --self-contained false -o "./publish"
```
