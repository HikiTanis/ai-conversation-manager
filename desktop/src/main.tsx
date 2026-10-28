import React from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import "./style.css";
const View =
  import.meta.env.DEV &&
  new URLSearchParams(location.search).has("updatePreview")
    ? React.lazy(() => import("./UpdatePreview"))
    : App;
createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <React.Suspense fallback={<p>加载中…</p>}>
      <View />
    </React.Suspense>
  </React.StrictMode>,
);
