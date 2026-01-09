import { apiRequest } from '../../shared/utils/apiClient';

interface AuthTokenResponse {
  token?: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  userName: string;
  email: string;
  password: string;
}

const ensureToken = (context: 'login' | 'register', response: AuthTokenResponse) => {
  if (typeof response.token !== 'string' || response.token.length === 0) {
    throw new Error(`Auth ${context} response did not include a token.`);
  }

  return response.token;
};

export const login = async ({ email, password }: LoginRequest): Promise<string> => {
  const response = await apiRequest<AuthTokenResponse, LoginRequest>('/api/auth/login', {
    method: 'POST',
    body: { email, password },
    authenticated: false
  });

  return ensureToken('login', response);
};

export const register = async (payload: RegisterRequest): Promise<string> => {
  const response = await apiRequest<AuthTokenResponse, RegisterRequest>('/api/auth/register', {
    method: 'POST',
    body: payload,
    authenticated: false
  });

  return ensureToken('register', response);
};

export interface ForgotPasswordRequest {
  email: string;
}

export const forgotPassword = async (payload: ForgotPasswordRequest): Promise<void> => {
  await apiRequest<void, ForgotPasswordRequest>('/api/auth/forgot-password', {
    method: 'POST',
    body: payload,
    authenticated: false
  });
};

export interface ResetPasswordRequest {
  email: string;
  token: string;
  newPassword: string;
}

export const resetPassword = async (payload: ResetPasswordRequest): Promise<void> => {
  await apiRequest<void, ResetPasswordRequest>('/api/auth/reset-password', {
    method: 'POST',
    body: payload,
    authenticated: false
  });
};

export interface UpdateProfileRequest {
  firstName: string;
  lastName: string;
}

export interface UpdateProfileResponse {
  id: string;
  firstName?: string;
  lastName?: string;
  email?: string;
}

export const updateProfile = async (payload: UpdateProfileRequest): Promise<UpdateProfileResponse> => {
  return apiRequest<UpdateProfileResponse, UpdateProfileRequest>('/api/auth/user', {
    method: 'PUT',
    body: payload,
    authenticated: true
  });
};
