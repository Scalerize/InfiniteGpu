import { useEffect, useState } from 'react';
import {
  BrowserRouter as Router,
  Routes,
  Route,
  Navigate
} from 'react-router-dom';
import { LoginPage } from './features/auth/components/LoginPage';
import { RegisterPage } from './features/auth/components/RegisterPage';
import { ForgotPasswordPage } from './features/auth/components/ForgotPasswordPage';
import { ResetPasswordPage } from './features/auth/components/ResetPasswordPage';
import { ProtectedAppShell } from './shared/layout/ProtectedAppShell';
import { DashboardPage } from './pages/DashboardPage';
import { TasksPage } from './pages/TasksPage';
import { RequestsPage } from './pages/RequestsPage';
import { FinancePage } from './pages/FinancePage';
import { DesktopBridge } from './shared/services/DesktopBridge';

function App() {
  const [isChecking, setIsChecking] = useState(true);

  useEffect(() => {
    // Check if desktop bridge is available
    const checkDesktopBridge = () => {
      if (!DesktopBridge.isAvailable()) {
        // Redirect to the web app
        window.location.href = 'https://infinite-gpu.scalerize.fr/';
        return;
      }
      setIsChecking(false);
    };

    checkDesktopBridge();
  }, []);

  // Show loading state while checking for desktop bridge
  if (isChecking) {
    return (
      <div className="flex h-screen items-center justify-center bg-slate-100 dark:bg-slate-950">
        <div className="text-center">
          <div className="mb-4 inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-r-transparent"></div>
          <p className="text-slate-600 dark:text-slate-400">Loading...</p>
        </div>
      </div>
    );
  }

  return (
    <Router>
      <Routes>
        <Route path="/auth/login" element={<LoginPage />} />
        <Route path="/auth/register" element={<RegisterPage />} />
        <Route path="/auth/forgot-password" element={<ForgotPasswordPage />} />
        <Route path="/auth/reset-password" element={<ResetPasswordPage />} />
        <Route path="/reset-password" element={<ResetPasswordPage />} />
        <Route element={<ProtectedAppShell />}>
          <Route index element={<DashboardPage />} />
          <Route path="tasks" element={<TasksPage />} />
          <Route path="requests" element={<RequestsPage />} />
          <Route path="finance" element={<FinancePage />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </Router>
  );
}

export default App;