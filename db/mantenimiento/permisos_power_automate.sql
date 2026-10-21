/* =====================================================================
   Permisos mínimos para el flujo de Power Automate del VoBo de bajas (migración 011).
   Lo ejecuta un DBA. El rol solo puede leer las dos vistas del VoBo y ejecutar los dos procedimientos;
   no tiene acceso al resto del esquema gd.
   Cambiar el nombre del login y poner una contraseña fuerte antes de ejecutar (o usar una cuenta de Windows de servicio).
   ===================================================================== */
-- 1. Login (en master) — solo si se usa autenticación de SQL Server:
-- CREATE LOGIN usrPowerAutomateGD WITH PASSWORD = N'<contraseña fuerte>', CHECK_POLICY = ON;

USE GobiernoDatos;
GO
IF DATABASE_PRINCIPAL_ID('gd_power_automate') IS NULL
    CREATE ROLE gd_power_automate;
GO
GRANT SELECT  ON gd.vw_recert_vobo_por_enviar TO gd_power_automate;
GRANT SELECT  ON gd.vw_recert_vobo_detalle    TO gd_power_automate;
GRANT EXECUTE ON gd.sp_vobo_marcar_enviada     TO gd_power_automate;
GRANT EXECUTE ON gd.sp_vobo_responder          TO gd_power_automate;
GO

-- 2. Usuario de la base para el login y su rol:
-- CREATE USER usrPowerAutomateGD FOR LOGIN usrPowerAutomateGD;
-- ALTER ROLE gd_power_automate ADD MEMBER usrPowerAutomateGD;
