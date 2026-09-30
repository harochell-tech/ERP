-- UX2-01 · Readable configuration (approved errata E-UX2-1…13): names, units and examples for the accounting policies' parameters,
-- names for account roles, descriptions for user roles, account role maps prepared from the screens, company and plant names
-- edited from the screens. The immutable catalogues are filled with their triggers disabled for this transaction only.

-- E-UX2-2: each parameter's Spanish name, unit, example (as shown on screen) and what it affects; each policy's name.
ALTER TABLE acc.accounting_policy ADD COLUMN name text;
ALTER TABLE acc.policy_parameter_definition
  ADD COLUMN label text,
  ADD COLUMN unit text,
  ADD COLUMN example text,
  ADD COLUMN affects text,
  ADD CONSTRAINT policy_parameter_definition_unit CHECK (unit IS NULL OR unit IN ('PERCENT', 'AMOUNT', 'DAYS', 'HOURS', 'OPTION'));

ALTER TABLE acc.accounting_policy DISABLE TRIGGER accounting_policy_immutable;
UPDATE acc.accounting_policy p SET name = v.name FROM (VALUES
  ('PURCHASING', 'Compras'),
  ('INVENTORY', 'Inventario'),
  ('POSTING', 'Contabilización'),
  ('TREASURY', 'Tesorería'),
  ('CREDIT', 'Crédito'),
  ('REVENUE_ACCOUNTING', 'Ingresos y entregas'),
  ('PRODUCTION', 'Producción')
) AS v (code, name) WHERE p.policy_code = v.code;
ALTER TABLE acc.accounting_policy ENABLE TRIGGER accounting_policy_immutable;

ALTER TABLE acc.policy_parameter_definition DISABLE TRIGGER policy_parameter_definition_immutable;
UPDATE acc.policy_parameter_definition d SET label = v.label, unit = v.unit, example = v.example, affects = v.affects FROM (VALUES
  ('receipt_tolerance_pct', 'Sobre-recepción permitida', 'PERCENT', '5 %',
   'Cuánto más de lo pedido puede recibir el almacén sin que el Aprobador de compras autorice el exceso.'),
  ('match_qty_tolerance_pct', 'Tolerancia de cantidad en el cruce de 3 vías', 'PERCENT', '2 %',
   'Diferencia de cantidad entre factura y recepción que se acepta sin excepción de cruce.'),
  ('match_price_tolerance_pct', 'Tolerancia de precio en el cruce de 3 vías', 'PERCENT', '1 %',
   'Diferencia de precio entre factura y orden de compra que se acepta sin excepción de cruce.'),
  ('match_amount_tolerance_abs', 'Tolerancia de importe en el cruce de 3 vías', 'AMOUNT', 'RD$ 100.00',
   'Diferencia de importe por línea que se acepta sin excepción de cruce, aunque supere los porcentajes.'),
  ('po_approval_limit', 'Límite de aprobación de órdenes de compra', 'AMOUNT', 'RD$ 500,000.00',
   'Total máximo de una orden que aprueba el Aprobador de compras; por encima la aprueba el Controller.'),
  ('po_approval_step_up_threshold', 'Reautenticación al aprobar órdenes de compra', 'AMOUNT', 'RD$ 250,000.00',
   'Total de orden a partir del cual aprobarla pide volver a iniciar sesión.'),
  ('inventory_adjustment_materiality', 'Materialidad de ajustes de inventario', 'AMOUNT', 'RD$ 25,000.00',
   'Valor de un ajuste de inventario a partir del cual lo aprueba el Controller.'),
  ('grni_aging_alert_days', 'Aviso de recibido no facturado', 'DAYS', '30 días',
   'Días desde la recepción sin factura del proveedor tras los cuales la conciliación GRNI-AGING avisa.'),
  ('invoice_price_variance_allocation_method', 'Asignación de la diferencia de precio de factura', 'OPTION', 'Según existencias',
   'Cómo se reparte entre inventario y variación la diferencia entre el precio facturado y el recibido.'),
  ('valuation_residual_account_role', 'Contrapartida del ajuste de valor residual', 'OPTION', 'Variación de precio de compra',
   'Cuenta contra la que se lleva el valor que queda sin existencias (R-06).'),
  ('rounding_difference_tolerance', 'Tolerancia de redondeo', 'AMOUNT', 'RD$ 1.00',
   'Diferencia máxima de un asiento que se lleva a Diferencia de redondeo en lugar de rechazarlo.'),
  ('late_entry_hours', 'Registro tardío', 'HOURS', '48 horas',
   'Horas entre el hecho y su registro a partir de las cuales el registro se marca como tardío.'),
  ('ap_aging_bucket_1_days', 'Antigüedad de CxP: fin del primer tramo', 'DAYS', '30 días',
   'Columnas del reporte de antigüedad de cuentas por pagar.'),
  ('ap_aging_bucket_2_days', 'Antigüedad de CxP: fin del segundo tramo', 'DAYS', '60 días',
   'Columnas del reporte de antigüedad de cuentas por pagar.'),
  ('ap_aging_bucket_3_days', 'Antigüedad de CxP: fin del tercer tramo', 'DAYS', '90 días',
   'Columnas del reporte de antigüedad de cuentas por pagar; lo que pasa de aquí es el último tramo.'),
  ('overdue_days_block', 'Atraso que detiene la aprobación automática de crédito', 'DAYS', '30 días',
   'Días de atraso del cliente a partir de los cuales un pedido pasa a aprobación de Crédito.'),
  ('ar_aging_bucket_1_days', 'Antigüedad de CxC: fin del primer tramo', 'DAYS', '30 días',
   'Columnas del reporte de antigüedad de cuentas por cobrar y del estado de cuenta.'),
  ('ar_aging_bucket_2_days', 'Antigüedad de CxC: fin del segundo tramo', 'DAYS', '60 días',
   'Columnas del reporte de antigüedad de cuentas por cobrar y del estado de cuenta.'),
  ('ar_aging_bucket_3_days', 'Antigüedad de CxC: fin del tercer tramo', 'DAYS', '90 días',
   'Columnas del reporte de antigüedad de cuentas por cobrar; lo que pasa de aquí es el último tramo.'),
  ('unbilled_delivery_presentation', 'Presentación de lo entregado sin facturar', 'OPTION', 'Activo de contrato',
   'Cuenta en la que se presenta lo entregado al cliente que aún no se ha facturado (P-16).'),
  ('unbilled_aging_alert_days', 'Aviso de entregado sin facturar', 'DAYS', '7 días',
   'Días de entregado sin facturar tras los cuales la conciliación CONTRACT-ASSET avisa.'),
  ('delivery_open_alert_hours', 'Aviso de conduce en tránsito', 'HOURS', '24 horas',
   'Horas de un conduce sin prueba de entrega tras las cuales la conciliación DELIVERY-OPEN avisa.'),
  ('authorization_expiry_alert_days', 'Aviso de autorización fiscal por vencer', 'DAYS', '30 días',
   'Días antes del vencimiento de una autorización fiscal en que la conciliación AUTH-EXPIRY avisa.'),
  ('usage_tolerance_pct', 'Tolerancia de consumo en producción', 'PERCENT', '3 %',
   'Diferencia del consumo real contra el teórico de la receta a partir de la cual USAGE-TOLERANCE avisa.')
) AS v (code, label, unit, example, affects) WHERE d.param_code = v.code;
ALTER TABLE acc.policy_parameter_definition ENABLE TRIGGER policy_parameter_definition_immutable;

-- E-UX2-5: each account role's name, as the screens show it.
ALTER TABLE fin.account_role ADD COLUMN name text;
ALTER TABLE fin.account_role DISABLE TRIGGER account_role_immutable;
UPDATE fin.account_role r SET name = v.name FROM (VALUES
  ('RAW_MATERIAL', 'Inventario de materia prima'),
  ('AP_CONTROL', 'Cuentas por pagar a proveedores'),
  ('GRNI', 'Recibido no facturado (GRNI)'),
  ('ITBIS_RECOVERABLE', 'ITBIS adelantado'),
  ('WITHHOLDING_PAYABLE', 'Retenciones por pagar'),
  ('PURCHASE_PRICE_VARIANCE', 'Variación de precio de compra'),
  ('MATERIAL_USAGE_VARIANCE', 'Variación de uso de material'),
  ('INVENTORY_ADJUSTMENT', 'Ajustes de inventario'),
  ('ROUNDING_DIFFERENCE', 'Diferencias de redondeo'),
  ('BANK', 'Bancos'),
  ('BANK_CHARGES', 'Cargos y comisiones bancarias'),
  ('MANUAL_ADJUSTMENT', 'Ajuste manual (técnico)'),
  ('AR_CONTROL', 'Cuentas por cobrar a clientes'),
  ('CONTRACT_ASSET', 'Activo de contrato (entregado no facturado)'),
  ('UNAPPLIED_RECEIPTS', 'Cobros no aplicados'),
  ('CASH_IN_TRANSIT', 'Efectivo y cheques en tránsito'),
  ('FINISHED_GOODS', 'Inventario de producto terminado'),
  ('FINISHED_GOODS_IN_TRANSIT', 'Producto terminado en tránsito'),
  ('COGS', 'Costo de ventas'),
  ('REVENUE_PRODUCT', 'Ingresos por venta de producto'),
  ('SALES_DISCOUNTS', 'Descuentos y rebajas sobre ventas'),
  ('ITBIS_PAYABLE', 'ITBIS por pagar'),
  ('WITHHOLDING_RECEIVABLE', 'Retenciones hechas por clientes'),
  ('TRANSIT_LOSS', 'Pérdida en tránsito'),
  ('MIGRATION_CLEARING', 'Contrapartida de saldos de apertura'),
  ('UNBILLED_RECEIVABLE', 'Cuenta por cobrar no facturada'),
  ('WIP', 'Producción en proceso'),
  ('CONVERSION_ABSORPTION', 'Absorción de costos de conversión'),
  ('MATERIAL_PRICE_VARIANCE', 'Variación de precio de materiales en producción'),
  ('PRODUCTION_SCRAP', 'Desperdicio de producto terminado'),
  ('STANDARD_REVALUATION', 'Revaluación por cambio de costo estándar')
) AS v (code, name) WHERE r.role_code = v.code;
ALTER TABLE fin.account_role ENABLE TRIGGER account_role_immutable;

-- E-UX2-12: what each user role is for, shown in Usuarios y roles and in role requests.
ALTER TABLE iam.role ADD COLUMN description text;
UPDATE iam.role r SET description = v.description FROM (VALUES
  ('COMPRADOR', 'Crea proveedores y órdenes de compra y las envía a aprobación.'),
  ('APROBADOR_COMPRAS', 'Aprueba órdenes de compra hasta su límite y autoriza recibir más de lo pedido.'),
  ('ALMACENISTA', 'Recibe materia prima contra órdenes de compra, crea materias primas y prepara correcciones de recepción.'),
  ('CUENTAS_POR_PAGAR', 'Registra, cruza y contabiliza las facturas de proveedores.'),
  ('CONTROLLER', 'Responsable contable: aprueba configuración, políticas, ajustes, pagos y cierres, y revierte documentos.'),
  ('ESPECIALISTA_FISCAL', 'Activa reglas fiscales y verifica autorizaciones fiscales de clientes.'),
  ('ANALISTA_FISCAL', 'Registra fuentes oficiales y configura reglas fiscales para que otro las active.'),
  ('ADMIN_SEGURIDAD', 'Solicita asignar o quitar roles a los usuarios.'),
  ('AUDITOR', 'Consulta todo, verifica la cadena de integridad y no ejecuta nada.'),
  ('SEGUNDO_APROBADOR_CIERRE', 'Da la segunda aprobación para reabrir un período cerrado.'),
  ('SEGUNDO_APROBADOR_SEGURIDAD', 'Aprueba o rechaza las solicitudes de roles.'),
  ('APROBADOR_POLITICAS', 'Aprueba políticas contables, estructuras de reporte, precios, costos estándar e inventario de apertura.'),
  ('TESORERO', 'Prepara pagos, importa estados bancarios, concilia y solicita cuentas bancarias de proveedores.'),
  ('PROBADOR', 'Solo en bases de prueba: actúa como identidades de prueba de cada rol.'),
  ('DIRECTOR', 'Consulta todas las pantallas y reportes; no ejecuta nada.'),
  ('CONTADOR', 'Prepara asientos de ajuste y mapas de cuentas; consulta la contabilidad.'),
  ('VENDEDOR', 'Crea clientes, cotizaciones y pedidos de venta.'),
  ('CREDITO', 'Decide el crédito de los pedidos, activa clientes y prepara sus condiciones.'),
  ('DESPACHO', 'Despacha pedidos, registra salidas por portón y pruebas de entrega, y maneja la flota.'),
  ('FACTURACION', 'Emite facturas y notas de crédito y registra los e-CF.'),
  ('COBROS', 'Registra cobros, depósitos, aplicaciones y retenciones de clientes.'),
  ('SUPERVISOR_PRODUCCION', 'Registra corridas y resúmenes de turno y prepara recetas.'),
  ('GERENTE_PLANTA', 'Aprueba recetas, contabiliza resúmenes de turno, da de baja lotes y mantiene máquinas y turnos.'),
  ('CALIDAD', 'Libera los lotes de producto terminado al terminar el curado.'),
  ('PROCESO_DIARIO', 'Identidad de servicio del proceso diario del sistema; no se asigna a personas.'),
  ('SUPERADMIN', 'Todos los permisos por 90 días como máximo; puede preparar y aprobar solo y cada caso queda marcado (ADM-2).')
) AS v (code, description) WHERE r.code = v.code;

-- E-UX2-6 (b), E-UX2-11: account role maps prepared from the screens; company legal name and plant names edited from the screens.
INSERT INTO iam.permission (permission_code, access) VALUES ('account_role_map:prepare', 'WRITE'), ('company:manage', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code FROM (VALUES
  ('CONTADOR', 'account_role_map:prepare'), ('CONTROLLER', 'account_role_map:prepare'), ('CONTROLLER', 'company:manage')
) AS v (role_code, permission_code) JOIN iam.role r ON r.code = v.role_code;

GRANT INSERT ON fin.account_role_map TO rochell_app;

-- md.company keeps its frozen shape; only the legal name changes (the RNC identifies the company).
CREATE FUNCTION md.company_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'md.company rows cannot be deleted';
  END IF;
  IF ROW(NEW.company_id, NEW.rnc) IS DISTINCT FROM ROW(OLD.company_id, OLD.rnc) THEN
    RAISE EXCEPTION 'md.company: only the legal name changes (E-UX2-11)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER company_guard BEFORE UPDATE OR DELETE ON md.company FOR EACH ROW EXECUTE FUNCTION md.company_guard();
ALTER TABLE md.company ADD CONSTRAINT company_legal_name CHECK (length(btrim(legal_name)) BETWEEN 1 AND 200);
GRANT UPDATE (legal_name) ON md.company TO rochell_app;
GRANT UPDATE (name) ON md.plant TO rochell_app;
