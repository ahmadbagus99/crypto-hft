/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        panel: "#121826",
        panelSoft: "#192132",
        exchangeGreen: "#16c784",
        exchangeRed: "#ea3943"
      }
    }
  },
  plugins: []
};
