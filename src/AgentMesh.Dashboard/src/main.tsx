import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { AppAuthProvider } from './auth/AuthProvider'
import { ErrorBoundary } from './components/ErrorBoundary'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary fallbackTitle="Application Error">
      <AppAuthProvider>
        <App />
      </AppAuthProvider>
    </ErrorBoundary>
  </StrictMode>,
)
