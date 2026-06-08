import { useState } from 'react';
import { useMsal } from '@azure/msal-react';
import { useConfig } from './ConfigContext';
import { getLoginRequest } from './authConfig';

export function Login() {
  const { instance, inProgress } = useMsal();
  const spaConfig = useConfig();
  const [error, setError] = useState('');

  function handleSignIn() {
    setError('');
    const loginRequest = getLoginRequest(spaConfig);
    instance.loginPopup(loginRequest).catch((err) => {
      console.error('Login error:', err);
      setError('Sign-in failed. Please try again.');
    });
  }

  return (
    <div className="auth-gate">
      <div className="auth-form">
        <h2>Multi-Agent Swarm</h2>
        <p>Sign in to access the swarm dashboard</p>
        {error && <p className="error-text">{error}</p>}
        <button
          onClick={handleSignIn}
          disabled={inProgress === 'login'}
          className="auth-button"
        >
          {inProgress === 'login' ? 'Signing in...' : 'Sign in with Microsoft'}
        </button>
      </div>
    </div>
  );
}
