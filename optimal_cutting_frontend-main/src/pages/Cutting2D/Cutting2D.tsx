import { Button, Card, Flex, Typography } from 'antd';
import { Cutting2DForm } from '../../components/forms/Cutting2DForm/Cutting2DForm';
import { useAppSelector } from '../../app/hooks';
import { selectCalculateData2D } from '../../features/cutting2DSlice';
import styles from './Cutting2D.module.css';

const downloadTextFile = (content: string, filename: string, type: string) => {
    const blob = new Blob([content], { type });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(url);
};

export const Cutting2D = () => {
    const cuttingData = useAppSelector(selectCalculateData2D);
    const hasResult = cuttingData.svg.length > 0 || cuttingData.placedParts.length > 0;

    return (
        <Flex className={styles.cutting2D} gap={24}>
            <Cutting2DForm />
            <Flex vertical className={styles.cutting2D__result} gap={16}>
                {hasResult ? (
                    <>
                        <Card className={styles.cutting2D__summary}>
                            <Flex gap={24} wrap='wrap'>
                                <Typography.Text>
                                    Размещено: {cuttingData.placedParts.length}
                                </Typography.Text>
                                <Typography.Text>
                                    Не размещено: {cuttingData.unplacedParts.length}
                                </Typography.Text>
                                <Typography.Text>
                                    Использование: {cuttingData.totalUtilization.toFixed(2)}%
                                </Typography.Text>
                            </Flex>
                            {cuttingData.unplacedParts.length > 0 && (
                                <Typography.Text type='warning'>
                                    Не удалось разместить детали:{' '}
                                    {cuttingData.unplacedParts.join(', ')}
                                </Typography.Text>
                            )}
                        </Card>
                        <Flex gap={12}>
                            <Button
                                disabled={!cuttingData.svg}
                                onClick={() =>
                                    downloadTextFile(
                                        cuttingData.svg,
                                        'cutting-2d.svg',
                                        'image/svg+xml'
                                    )
                                }
                            >
                                Скачать SVG
                            </Button>
                            <Button
                                disabled={!cuttingData.dxf}
                                onClick={() =>
                                    downloadTextFile(
                                        cuttingData.dxf,
                                        'cutting-2d.dxf',
                                        'application/dxf'
                                    )
                                }
                            >
                                Скачать DXF
                            </Button>
                        </Flex>
                        {cuttingData.svg && (
                            <div
                                className={styles.cutting2D__svg}
                                dangerouslySetInnerHTML={{ __html: cuttingData.svg }}
                            />
                        )}
                    </>
                ) : (
                    <Flex className={styles.cutting2D__empty} align='center' justify='center'>
                        Выберите детали и заготовку, чтобы построить схему 2D-раскроя.
                    </Flex>
                )}
            </Flex>
        </Flex>
    );
};
