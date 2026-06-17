# Mejoras del Oloráculo (fork)

Este documento resume los arreglos y mejoras aplicados al modelo de predicción sobre el
proyecto original [MarianoVilla/Oloraculo](https://github.com/MarianoVilla/Oloraculo).

## Bugs corregidos

### 1. Dixon-Coles estaba desactivado
`GoalModel` definía `LowScoreRho = 0.00`, lo que anulaba por completo la corrección de
marcadores bajos de Dixon-Coles (el factor τ valía 1 para todos los marcadores). El modelo
**anunciaba** "Grilla de marcadores Dixon-Coles" pero no la aplicaba, subestimando empates y
partidos de pocos goles.

**Solución:** ahora `rho` se **calibra por máxima verosimilitud** sobre la ventana histórica
(ponderada por recencia) en `GoalModel.FitRho`, con un prior razonable (`-0.05`) cuando hay
pocos partidos de marcador bajo. Ver `Predictors/GoalModel.cs` y
`Probability/ProbabilityHelper.cs`.

### 2. El "Oráculo final" podía contradecirse
`FinalPredictionSelector` aplicaba un sesgo Elo/FIFA que cambiaba las probabilidades 1X2 pero
**dejaba el marcador más probable del modelo anterior**, de modo que el marcador mostrado podía
contradecir las probabilidades de la cabecera.

**Solución:** el marcador mostrado se elige ahora como el marcador exacto más probable
**consistente con el resultado más probable del ensemble** (`MostLikelyScorelineForOutcome`).
Hay un test de regresión específico para esto.

## Mejoras del modelo

### 3. Ensemble ponderado en lugar de elegir un solo modelo
Antes, el "Oráculo final" elegía el modelo de mayor prioridad utilizable y descartaba el resto
(más un pequeño sesgo Elo/FIFA codificado a mano). Ahora **combina todos los modelos utilizables**
en un ensemble ponderado:

```
peso(modelo) = prior(prioridad del modelo) × fiabilidad(precisión histórica del modelo)
```

El modelo base (uniforme) y los modelos degradados quedan fuera de la mezcla. El modelo más rico
disponible sigue anclando el marcador y los goles esperados.

### 4. El modelo ahora aprende de verdad de sus errores (bucle cerrado)
El sistema ya calculaba métricas (Brier / RPS / LogLoss) de cada predicción, pero **nada de eso
realimentaba al modelo**: la selección era por prioridad fija. Ahora:

1. Cada partido jugado se evalúa **por modelo** (no solo el "Oráculo final"), comparando la
   predicción **pre-partido** de cada modelo con el resultado real
   (`EvaluationService.EvaluateLatestSnapshotAsync`).
2. `EvaluationService.ComputeReliabilityWeights` convierte ese historial en un multiplicador de
   fiabilidad por modelo: menor RPS ⇒ más peso. Las muestras pequeñas se encogen hacia la media
   global para no sobre-reaccionar a aciertos/fallos puntuales.
3. `FinalPredictionSelector` usa esos multiplicadores al combinar el ensemble.

Resultado: a medida que entran resultados reales, los modelos que aciertan más ganan influencia
y los que fallan la pierden, automáticamente.

### 5. Conectado a la API (datos reales, no manual)
La parte de "meter el resultado real para que el modelo aprenda" sigue disponible de forma
manual, pero ahora el pipeline automático (`ReadmeSnapshotExportService.ExportAsync`) tira de
**API-Football** para:

- Traer los **resultados reales** de los partidos jugados (`RefreshFixturesAsync`).
- Disparar la **evaluación por modelo** de esos partidos.
- Recalcular los **pesos de fiabilidad** en la siguiente predicción.

Además se guardan los snapshots **pre-partido de cada modelo componente**, para que cuando el
partido se juegue cada modelo pueda ser calificado individualmente y el ensemble aprenda.

API-Football también aporta lesiones, alineaciones, cuotas y estadísticas de jugadores que ya se
ingieren en el contexto del partido.

### 6. Lectura automática de resultados al terminar cada partido (football-data.org)
Nueva integración con **football-data.org** (`Services/FootballDataService.cs`) y un servicio en
segundo plano (`Services/ResultIngestionBackgroundService.cs`) que:

1. Cada `ResultPollIntervalMinutes` (15 min por defecto) busca partidos que **ya deberían haber
   terminado**: estima el fin como `kickoff + ResultPollMatchDurationMinutes` (150 min = 90' +
   descanso + descuento + margen), así nunca pide un resultado que no puede existir todavía.
2. Para esos partidos consulta football-data.org, mapea el resultado a nuestro fixture (por nombre
   de equipo normalizado, en cualquier orientación) e **ingiere el marcador real automáticamente**.
3. Dispara la evaluación de esos partidos y, con ello, la **recalibración de los pesos** del
   ensemble. Todo sin intervención manual.

El token se configura en `Oloraculo:FootballDataApiKey` (ver más abajo). Se puede desactivar con
`Oloraculo:ResultPollEnabled = false`.

### 7. Web renovada
La portada (`Components/Pages/Home.razor`) pasó a ser un dashboard: hero, **favoritos al título**
con barras de probabilidad y banderas (del último snapshot de torneo), panel de **aprendizaje del
modelo** (precisión por modelo cuando hay resultados) y un resumen visual de cómo predice. Estilos
nuevos en `wwwroot/app.css`.

## Cómo configurar la API key (gratis)

### football-data.org (resultados automáticos)
Ya viene configurada en `appsettings.json` (`Oloraculo:FootballDataApiKey`). Para el Mundial, ajusta
`Oloraculo:FootballDataCompetition` (código de competición de football-data.org, p. ej. `WC`).
Por seguridad puedes mover el token a una variable de entorno `Oloraculo__FootballDataApiKey` o a
user-secrets y quitarlo del `appsettings.json`.

### API-Football (lesiones, alineaciones, cuotas)

1. Crea una cuenta gratuita en **https://www.api-football.com/** (o vía RapidAPI / api-sports.io).
   El plan gratuito da ~100 peticiones/día.
2. Copia tu API key.
3. Configúrala sin commitearla, por cualquiera de estas vías:

   **Variable de entorno** (recomendado):
   ```powershell
   $env:Oloraculo__ApiFootballApiKey = "TU_API_KEY"
   ```

   **o user-secrets** (en `Oloraculo.Web`):
   ```powershell
   dotnet user-secrets init
   dotnet user-secrets set "Oloraculo:ApiFootballApiKey" "TU_API_KEY"
   ```

   **o `appsettings.Development.json`** (este archivo está en `.gitignore`):
   ```json
   { "Oloraculo": { "ApiFootballApiKey": "TU_API_KEY" } }
   ```
4. Ajusta `Oloraculo:ApiFootballLeagueId` y `Oloraculo:ApiFootballSeason` al Mundial 2026 según
   los IDs de API-Football. Sin key, el sistema sigue funcionando con los CSV incluidos.

## Tests

`dotnet test` — 127 pruebas, todas en verde. Se añadieron tests para el ensemble ponderado, la
consistencia marcador↔probabilidades y los pesos de fiabilidad (el aprendizaje).
