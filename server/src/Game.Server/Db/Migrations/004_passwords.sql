-- 로그인 비밀번호. NULL = 비밀번호가 생기기 전의 옛 계정(서버 PC에서 처음 접속할 때 정한다).
alter table accounts add column password_hash text;
