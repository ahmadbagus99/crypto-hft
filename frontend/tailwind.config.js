/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        // Surfaces run as a ramp from the page void up to a raised panel. Keeping them
        // ordered (rather than one-off hexes) is what stops panels from muddying together.
        void: "#05070d",
        surface: "#0a0e17",
        panel: "#0d1320",
        panelSoft: "#131b2c",
        panelRaised: "#182136",
        hairline: "#1e2941",
        // Cyan is the system's own voice: anything the engine is telling you about itself.
        cyan: {
          DEFAULT: "#22d3ee",
          soft: "#67e8f9",
          deep: "#0e7490",
        },
        // Money colors stay separate from the system color, so profit and loss never
        // compete with UI chrome for attention.
        exchangeGreen: "#00e29a",
        exchangeRed: "#ff4d6a",
        warn: "#ffb020",
      },
      fontFamily: {
        // Monospace throughout: a trading surface reads as columns, and proportional
        // digits make those columns wobble every time a value ticks.
        mono: ["ui-monospace", "SFMono-Regular", "Menlo", "Consolas", "monospace"],
        display: ["ui-monospace", "SFMono-Regular", "Menlo", "Consolas", "monospace"],
      },
      boxShadow: {
        glow: "0 0 0 1px rgba(34,211,238,0.18), 0 0 24px -6px rgba(34,211,238,0.35)",
        glowSoft: "0 0 20px -8px rgba(34,211,238,0.5)",
        panel: "0 1px 0 0 rgba(255,255,255,0.03) inset, 0 18px 40px -24px rgba(0,0,0,0.9)",
        up: "0 0 18px -6px rgba(0,226,154,0.55)",
        down: "0 0 18px -6px rgba(255,77,106,0.55)",
      },
      keyframes: {
        pulseDot: {
          "0%, 100%": { opacity: "1", transform: "scale(1)" },
          "50%": { opacity: "0.35", transform: "scale(0.82)" },
        },
        sweep: {
          "0%": { transform: "translateX(-100%)" },
          "100%": { transform: "translateX(100%)" },
        },
        riseIn: {
          "0%": { opacity: "0", transform: "translateY(6px)" },
          "100%": { opacity: "1", transform: "translateY(0)" },
        },
      },
      animation: {
        pulseDot: "pulseDot 1.8s ease-in-out infinite",
        sweep: "sweep 2.6s ease-in-out infinite",
        riseIn: "riseIn 260ms ease-out both",
      },
    },
  },
  plugins: [],
};
