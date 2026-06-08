import { useState, useEffect, useRef, useCallback } from 'react';
import toast from 'react-hot-toast';
import { TemplateEditor } from './TemplateEditor';
import { useAuthToken } from '../auth/useAuthToken';

const API_BASE = import.meta.env.VITE_API_URL ?? '';

interface TemplateOption {
  key: string;
  name: string;
  description: string;
}

interface SwarmControlsProps {
  onStart: (swarmId: string) => void;
}

export function SwarmControls({ onStart }: SwarmControlsProps) {
  const { getToken } = useAuthToken();
  const [goal, setGoal] = useState('');
  const [templates, setTemplates] = useState<TemplateOption[]>([]);
  const [template, setTemplate] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showEditor, setShowEditor] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const getAuthHeaders = useCallback(async (): Promise<Record<string, string>> => {
    const token = await getToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
  }, [getToken]);

  async function fetchTemplates() {
    const headers = await getAuthHeaders();
    fetch(`${API_BASE}/api/swarm/templates`, { headers })
      .then((res) => (res.ok ? res.json() : []))
      .then((data) => {
        const fetched: TemplateOption[] = Array.isArray(data) ? data : (data.templates ?? []);
        setTemplates(fetched);
        if (fetched.length > 0 && !template) {
          setTemplate(fetched[0].key);
        }
      })
      .catch(() => null);
  }

  useEffect(() => {
    fetchTemplates();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function handleDeploy(file: File) {
    setError(null);
    const headers = await getAuthHeaders();
    const formData = new FormData();
    formData.append('file', file);
    try {
      const res = await fetch(`${API_BASE}/api/swarm/templates/deploy`, {
        method: 'POST',
        headers,
        body: formData,
      });
      if (!res.ok) {
        const detail = await res.json().catch(() => ({}));
        throw new Error(detail.detail || `Deploy failed: ${res.status}`);
      }
      const result = await res.json();
      toast.success(`Template "${result.name || result.key}" deployed`);
      fetchTemplates();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Deploy failed';
      toast.error(msg);
      setError(msg);
    }
  }

  async function handleStart() {
    if (!goal.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const headers = await getAuthHeaders();
      const res = await fetch(`${API_BASE}/api/swarm`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...headers,
        },
        body: JSON.stringify({ goal: goal.trim(), templateKey: template }),
      });
      if (!res.ok) {
        throw new Error(`Server error: ${res.status}`);
      }
      const data = await res.json();
      onStart(data.swarmId);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to start swarm');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="swarm-controls">
      <textarea
        placeholder="Enter your goal..."
        value={goal}
        onChange={(e) => setGoal(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            handleStart();
          }
        }}
        disabled={loading}
        className="goal-input goal-input--textarea"
        rows={3}
      />
      <div className="swarm-controls__actions" data-testid="swarm-actions">
        <input
          ref={fileInputRef}
          type="file"
          accept=".zip"
          style={{ display: 'none' }}
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) handleDeploy(file);
            e.target.value = '';
          }}
        />
        <button
          onClick={() => fileInputRef.current?.click()}
          className="te-small-dark-btn"
          title="Deploy template pack"
          aria-label="Deploy template pack"
        >
          &#8679;
        </button>
        <select
          value={template}
          onChange={(e) => setTemplate(e.target.value)}
          disabled={loading || templates.length === 0}
          className="template-select"
        >
          {templates.map((t) => (
            <option key={t.key} value={t.key}>
              {t.name}
            </option>
          ))}
        </select>
        <button
          onClick={() => setShowEditor(true)}
          className="te-small-dark-btn"
          title="Edit templates"
          aria-label="Edit templates"
        >
          &#9998;
        </button>
        <button onClick={handleStart} disabled={loading || !goal.trim()} className="start-button">
          {loading ? 'Starting...' : 'Start Swarm'}
        </button>
      </div>
      {error && <p className="error-text">{error}</p>}
      {showEditor && <TemplateEditor onClose={() => setShowEditor(false)} />}
    </div>
  );
}
